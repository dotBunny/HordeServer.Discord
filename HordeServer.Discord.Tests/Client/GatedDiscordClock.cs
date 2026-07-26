// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Client;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// A clock whose waits do not finish until the test says so.
	/// </summary>
	/// <remarks>
	/// <see cref="FakeDiscordClock"/> returns from every wait immediately, which is right for the rate limiter -
	/// nothing else is running - and wrong for the gateway, where a heartbeat loop runs concurrently with the receive
	/// loop. Against an instant clock that loop would spin at the speed of the CPU. Here it parks on its first wait
	/// and the test advances it one beat at a time, which is both deterministic and the only way to hold a heartbeat
	/// unacknowledged long enough to be declared dead.
	/// </remarks>
	sealed class GatedDiscordClock : IDiscordClock
	{
		readonly object _lock = new object();
		readonly Queue<Pending> _pending = new Queue<Pending>();
		readonly List<TimeSpan> _requested = new List<TimeSpan>();

		TaskCompletionSource _somethingIsWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>
		/// How long a <see cref="ReleaseNextAsync"/> waits for something to ask for a delay before giving up.
		/// </summary>
		/// <remarks>A wedged test should fail, not hang a CI run until it is killed.</remarks>
		public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10.0);

		public DateTime UtcNow { get; private set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		/// <summary>
		/// Every delay that has been asked for, in order.
		/// </summary>
		public IReadOnlyList<TimeSpan> Requested
		{
			get
			{
				lock (_lock)
				{
					return _requested.ToArray();
				}
			}
		}

		public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
		{
			if (delay <= TimeSpan.Zero)
			{
				return Task.CompletedTask;
			}

			TaskCompletionSource source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			CancellationTokenRegistration registration = cancellationToken.Register(() => source.TrySetCanceled());

			lock (_lock)
			{
				_requested.Add(delay);
				_pending.Enqueue(new Pending(delay, source, registration));
				_somethingIsWaiting.TrySetResult();
			}

			return source.Task;
		}

		/// <summary>
		/// Waits for something to ask for a delay, then lets that one delay through.
		/// </summary>
		/// <returns>The delay that was released.</returns>
		public async Task<TimeSpan> ReleaseNextAsync()
		{
			while (true)
			{
				if (TryReleaseNext(out TimeSpan released))
				{
					return released;
				}

				Task waitForOne;

				lock (_lock)
				{
					_somethingIsWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
					waitForOne = _somethingIsWaiting.Task;
				}

				await waitForOne.WaitAsync(Patience);
			}
		}

		/// <summary>
		/// Lets one delay through if anything is waiting, without blocking if nothing is.
		/// </summary>
		/// <param name="released">The delay that was released.</param>
		/// <returns>False if nothing was waiting.</returns>
		public bool TryReleaseNext(out TimeSpan released)
		{
			Pending pending;

			lock (_lock)
			{
				if (_pending.Count == 0)
				{
					released = TimeSpan.Zero;
					return false;
				}

				pending = _pending.Dequeue();
				UtcNow += pending.Delay;
			}

			pending.Registration.Dispose();
			pending.Source.TrySetResult();

			released = pending.Delay;
			return true;
		}

		/// <summary>
		/// Releases delays, in the order they were asked for, until the given work finishes.
		/// </summary>
		/// <remarks>
		/// The heartbeat loop is always waiting on something, so a test that wants to unblock one *particular* wait
		/// cannot simply release the next one - it would release the heartbeat's and leave the wait it cared about
		/// still pending. Releasing until the work completes sidesteps the ordering entirely, and
		/// <see cref="Requested"/> still records every delay that was asked for so the interesting one can be
		/// asserted on afterwards.
		/// </remarks>
		/// <param name="work">The work to wait for.</param>
		public async Task ReleaseUntilAsync(Task work)
		{
			DateTime deadline = DateTime.UtcNow + Patience;

			while (!work.IsCompleted && DateTime.UtcNow < deadline)
			{
				if (!TryReleaseNext(out _))
				{
					// Nothing waiting yet. Yield rather than spin - whatever is going to ask for a delay is running
					// on another task and needs the scheduler.
					await Task.Yield();
				}
			}

			await work.WaitAsync(Patience);
		}

		sealed record Pending(TimeSpan Delay, TaskCompletionSource Source, CancellationTokenRegistration Registration);
	}
}
