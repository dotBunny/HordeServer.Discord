// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HordeServer.Discord.Client;

namespace HordeServer.Discord.Notifications
{
	/// <summary>
	/// Remembers what has already been reported, so a condition that persists is announced once rather than on every
	/// poll.
	/// </summary>
	/// <remarks>
	/// Some of Horde's notifications describe a *state* rather than an event. Configuration update failures are the
	/// clearest case: the config service re-reads its sources on a ticker and reports the same failure every time
	/// until somebody fixes it, so a sink that posts unconditionally turns one broken include into a channel full of
	/// identical messages. The Slack sink solves this by keeping a digest of the last message per channel and event in
	/// MongoDB and only sending when the digest changes.
	///
	/// This is the same idea held in memory. It is deliberately *not* the Mongo collection: nothing here needs to edit
	/// a message that was already posted, which is the other half of what Slack's message state exists for and the
	/// half that genuinely needs persistence. That arrives in Phase 4 with issue triage, and this can fold into it.
	///
	/// Two consequences of being in memory, both accepted:
	///
	/// <list type="bullet">
	/// <item>A server restart forgets everything, so a still-broken config is announced once more afterwards. That is
	/// arguably the right behaviour anyway - the new process has genuinely not said it yet.</item>
	/// <item>Two Horde servers sharing a Discord channel would each announce. Horde does run multi-instance, but only
	/// one instance owns the config service ticker, so in practice only one of them reports.</item>
	/// </list>
	///
	/// Entries expire and the store is capped, because the event id space is not bounded - test health reports are
	/// keyed per test, and a farm can retire tests faster than it restarts servers.
	/// </remarks>
	public sealed class DiscordRepeatFilter
	{
		/// <summary>
		/// How long an entry is remembered before the condition is treated as new again.
		/// </summary>
		/// <remarks>
		/// Long, because the thing being suppressed is a repeat rather than a rate. A week means a config that has
		/// been broken since last Tuesday gets one fresh mention rather than silence, which is closer to useful than
		/// to noisy.
		/// </remarks>
		public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7.0);

		/// <summary>
		/// Most entries kept before the oldest are discarded.
		/// </summary>
		public const int Capacity = 1024;

		readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
		readonly IDiscordClock _clock;
		readonly object _pruneLock = new object();

		sealed record Entry(string Digest, DateTime TimeUtc);

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="clock">Source of time, so expiry can be tested without waiting for it.</param>
		public DiscordRepeatFilter(IDiscordClock? clock = null)
			=> _clock = clock ?? DiscordSystemClock.Instance;

		/// <summary>
		/// Number of events currently remembered.
		/// </summary>
		public int Count => _entries.Count;

		/// <summary>
		/// Records that an event is in a particular state, and reports whether that is news.
		/// </summary>
		/// <param name="eventId">Identifies the thing being reported on - a config file, a test, a device pool.</param>
		/// <param name="state">Everything about the current state that would change what the message says.</param>
		/// <returns>True when this state has not been reported, and so should be sent.</returns>
		public bool RecordIfChanged(string eventId, string state)
		{
			string digest = Digest(state);
			DateTime now = _clock.UtcNow;
			bool changed = true;

			_entries.AddOrUpdate(
				eventId,
				_ => new Entry(digest, now),
				(_, existing) =>
				{
					// An entry that has aged out is treated as absent rather than removed on read, which keeps this
					// a single atomic operation. Pruning is what actually reclaims it.
					changed = existing.Digest != digest || existing.TimeUtc + Lifetime <= now;
					return changed ? new Entry(digest, now) : existing;
				});

			Prune(now);
			return changed;
		}

		/// <summary>
		/// Records that an event was reported, without a state to compare against next time.
		/// </summary>
		/// <remarks>
		/// For notifications where something upstream has already decided this is worth sending - the test health
		/// service will not call us twice for an unchanged report - and all that is wanted is to know later whether
		/// anything was ever said, so a recovery message has something to correct.
		/// </remarks>
		/// <param name="eventId">Identifies the thing being reported on.</param>
		public void Record(string eventId)
		{
			DateTime now = _clock.UtcNow;
			_entries[eventId] = new Entry(String.Empty, now);
			Prune(now);
		}

		/// <summary>
		/// Forgets an event, and reports whether anything was remembered about it.
		/// </summary>
		/// <remarks>
		/// The return value is what makes recovery messages possible: "it is fixed now" is worth saying to a channel
		/// that was told it was broken, and is noise in one that never heard about it.
		/// </remarks>
		/// <param name="eventId">Identifies the thing being reported on.</param>
		/// <returns>True if the event had been reported and had not yet expired.</returns>
		public bool Clear(string eventId)
			=> _entries.TryRemove(eventId, out Entry? entry) && entry.TimeUtc + Lifetime > _clock.UtcNow;

		/// <summary>
		/// Drops expired entries, and the oldest ones if the store is still over capacity.
		/// </summary>
		/// <param name="now">Current time.</param>
		void Prune(DateTime now)
		{
			if (_entries.Count <= Capacity)
			{
				return;
			}

			// Only one thread needs to do this, and a caller that finds it in progress can carry on - being briefly
			// over capacity costs nothing, whereas blocking a notification on housekeeping would be absurd.
			if (!Monitor.TryEnter(_pruneLock))
			{
				return;
			}

			try
			{
				foreach ((string eventId, Entry entry) in _entries)
				{
					if (entry.TimeUtc + Lifetime <= now)
					{
						_entries.TryRemove(eventId, out _);
					}
				}

				int excess = _entries.Count - Capacity;

				if (excess > 0)
				{
					foreach ((string eventId, Entry _) in _entries.OrderBy(x => x.Value.TimeUtc).Take(excess))
					{
						_entries.TryRemove(eventId, out _);
					}
				}
			}
			finally
			{
				Monitor.Exit(_pruneLock);
			}
		}

		/// <summary>
		/// Reduces a message state to a fixed-size value.
		/// </summary>
		/// <remarks>
		/// Hashed rather than stored, because the state being compared is things like a stack trace or a parser error
		/// with an include stack behind it. Keeping the text would make the memory footprint a function of how badly
		/// broken the farm is, which is the wrong way round.
		/// </remarks>
		/// <param name="state">Text describing the state.</param>
		/// <returns>A hex digest of the state.</returns>
		public static string Digest(string state)
			=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
	}
}
