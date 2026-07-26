// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// Keeps outgoing Discord requests inside the API's rate limits, and retries the ones that come back throttled.
	/// </summary>
	/// <remarks>
	/// Not a Polly retry policy. Discord publishes its limits in response headers and expects clients to obey them
	/// proactively: <c>X-RateLimit-Remaining</c> and <c>X-RateLimit-Reset-After</c> per route, plus a ceiling of
	/// <see cref="DefaultGlobalRequestsPerSecond"/> requests per second across the whole bot token. Retrying after
	/// the fact would still deliver, but a build farm bursting on a broken stream would spend its time collecting
	/// 429s, and a bot that ignores them risks a much longer ban.
	///
	/// Three behaviours are worth knowing about:
	///
	/// - A **shared**-scope 429 (<c>X-RateLimit-Scope: shared</c>) is not our allowance running out; it is
	///   contention on a resource someone else is also hammering. It is waited out but deliberately not recorded
	///   against our bucket, which would otherwise throttle us for a limit we did not hit.
	/// - Routes flagged <see cref="DiscordRoute.ExemptFromGlobalLimit"/> - interaction responses - skip the global
	///   ceiling entirely, so triage stays responsive while notifications are being throttled.
	/// - A retry-after longer than <see cref="MaxRetryDelay"/> is not waited out. The 429 is handed back to the
	///   caller instead: holding a notification for the length of a cloudflare ban helps nobody, and the caller can
	///   at least log a real status code.
	/// </remarks>
	public sealed class DiscordRateLimiter
	{
		/// <summary>
		/// Requests per second allowed across a bot token, independent of any per-route limit.
		/// </summary>
		public const int DefaultGlobalRequestsPerSecond = 50;

		/// <summary>
		/// How many times a single request is sent before its failure is handed back to the caller.
		/// </summary>
		public const int DefaultMaxAttempts = 4;

		/// <summary>
		/// Longest delay that will be waited out rather than reported as a failure.
		/// </summary>
		public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60.0);

		static readonly TimeSpan s_globalWindow = TimeSpan.FromSeconds(1.0);
		static readonly TimeSpan s_fallbackRetryDelay = TimeSpan.FromSeconds(1.0);

		readonly IDiscordClock _clock;
		readonly ILogger _logger;
		readonly int _globalRequestsPerSecond;
		readonly int _maxAttempts;

		readonly ConcurrentDictionary<string, Bucket> _buckets = new ConcurrentDictionary<string, Bucket>(StringComparer.Ordinal);

		readonly SemaphoreSlim _globalGate = new SemaphoreSlim(1, 1);
		readonly Queue<DateTime> _globalWindowRequests = new Queue<DateTime>();
		readonly object _globalResumeLock = new object();
		DateTime _globalResumeAt = DateTime.MinValue;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="logger">Logger for throttling diagnostics.</param>
		/// <param name="clock">Clock to measure against. Defaults to the system clock.</param>
		/// <param name="globalRequestsPerSecond">Global ceiling to enforce. Defaults to Discord's documented 50.</param>
		/// <param name="maxAttempts">Attempts per request, including the first.</param>
		public DiscordRateLimiter(ILogger logger, IDiscordClock? clock = null, int globalRequestsPerSecond = DefaultGlobalRequestsPerSecond, int maxAttempts = DefaultMaxAttempts)
		{
			_logger = logger;
			_clock = clock ?? DiscordSystemClock.Instance;
			_globalRequestsPerSecond = Math.Max(1, globalRequestsPerSecond);
			_maxAttempts = Math.Max(1, maxAttempts);
		}

		/// <summary>
		/// Number of route buckets currently being tracked. Diagnostics and tests only.
		/// </summary>
		public int TrackedBucketCount => _buckets.Count;

		/// <summary>
		/// Sends a request, waiting first for the limits to allow it and retrying if the response says to.
		/// </summary>
		/// <param name="route">Route the request belongs to, which determines its bucket.</param>
		/// <param name="sendAsync">
		/// Sends the request. Called once per attempt, and must build a fresh <c>HttpRequestMessage</c> each time -
		/// a request message cannot be sent twice.
		/// </param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>
		/// The final response, which the caller owns and must dispose. A response is still returned when every
		/// attempt was throttled, so the caller can report the failure with a real status code rather than an
		/// exception that says nothing.
		/// </returns>
		public async Task<HttpResponseMessage> SendAsync(DiscordRoute route, Func<CancellationToken, Task<HttpResponseMessage>> sendAsync, CancellationToken cancellationToken)
		{
			Bucket bucket = _buckets.GetOrAdd(route.Key, static key => new Bucket(key));

			for (int attempt = 1; ; attempt++)
			{
				if (!route.ExemptFromGlobalLimit)
				{
					await WaitForGlobalSlotAsync(cancellationToken);
				}

				await bucket.WaitForSlotAsync(_clock, cancellationToken);

				HttpResponseMessage response = await sendAsync(cancellationToken);
				bucket.Update(response, _clock.UtcNow);

				if (response.StatusCode != HttpStatusCode.TooManyRequests)
				{
					return response;
				}

				TimeSpan retryAfter = GetRetryAfter(response);

				if (IsGlobalThrottle(response))
				{
					SetGlobalCooldown(retryAfter);
				}

				if (attempt >= _maxAttempts || retryAfter > MaxRetryDelay)
				{
					_logger.LogError("Discord rate limited {Route}; giving up after {Attempts} attempt(s), retry-after was {RetryAfter}",
						route.Key, attempt, retryAfter);
					return response;
				}

				_logger.LogWarning("Discord rate limited {Route} (attempt {Attempt} of {MaxAttempts}, scope {Scope}); retrying in {RetryAfter}",
					route.Key, attempt, _maxAttempts, GetHeader(response, "X-RateLimit-Scope") ?? "user", retryAfter);

				response.Dispose();
				await _clock.DelayAsync(retryAfter, cancellationToken);
			}
		}

		async Task WaitForGlobalSlotAsync(CancellationToken cancellationToken)
		{
			// The gate is held across the waits on purpose. Everything queued behind it has to wait for the same
			// window anyway, and serialising here is what stops fifty threads all deciding there is one slot left.
			await _globalGate.WaitAsync(cancellationToken);
			try
			{
				while (true)
				{
					DateTime now = _clock.UtcNow;
					DateTime resumeAt;

					lock (_globalResumeLock)
					{
						resumeAt = _globalResumeAt;
					}

					if (now < resumeAt)
					{
						await _clock.DelayAsync(resumeAt - now, cancellationToken);
						continue;
					}

					while (_globalWindowRequests.Count > 0 && now - _globalWindowRequests.Peek() >= s_globalWindow)
					{
						_globalWindowRequests.Dequeue();
					}

					if (_globalWindowRequests.Count < _globalRequestsPerSecond)
					{
						_globalWindowRequests.Enqueue(now);
						return;
					}

					await _clock.DelayAsync(_globalWindowRequests.Peek() + s_globalWindow - now, cancellationToken);
				}
			}
			finally
			{
				_globalGate.Release();
			}
		}

		void SetGlobalCooldown(TimeSpan retryAfter)
		{
			DateTime resumeAt = _clock.UtcNow + retryAfter;

			lock (_globalResumeLock)
			{
				if (resumeAt <= _globalResumeAt)
				{
					return;
				}

				_globalResumeAt = resumeAt;
			}

			_logger.LogWarning("Discord applied a global rate limit; pausing non-interaction requests for {RetryAfter}", retryAfter);
		}

		static bool IsGlobalThrottle(HttpResponseMessage response)
			=> String.Equals(GetHeader(response, "X-RateLimit-Scope"), "global", StringComparison.OrdinalIgnoreCase)
				|| String.Equals(GetHeader(response, "X-RateLimit-Global"), "true", StringComparison.OrdinalIgnoreCase);

		static TimeSpan GetRetryAfter(HttpResponseMessage response)
		{
			// Reset-After is the more precise of the two - fractional seconds, where Retry-After is whole ones
			// rounded up. Both are read raw rather than through the typed header, which cannot parse a fraction.
			if (TryGetSeconds(response, "X-RateLimit-Reset-After", out TimeSpan resetAfter))
			{
				return resetAfter;
			}

			if (TryGetSeconds(response, "Retry-After", out TimeSpan retryAfter))
			{
				return retryAfter;
			}

			return s_fallbackRetryDelay;
		}

		static bool TryGetSeconds(HttpResponseMessage response, string name, out TimeSpan value)
		{
			string? raw = GetHeader(response, name);

			if (raw != null && Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0.0)
			{
				value = TimeSpan.FromSeconds(seconds);
				return true;
			}

			value = TimeSpan.Zero;
			return false;
		}

		static string? GetHeader(HttpResponseMessage response, string name)
			=> response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

		/// <summary>
		/// State of one route's rate limit window.
		/// </summary>
		sealed class Bucket
		{
			readonly string _key;

			// Serialises whoever is waiting for a slot. Held across the wait, because everyone behind it is waiting
			// for the same window to expire.
			readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

			// Guards the two fields below and is never held across an await, so a response arriving mid-wait can
			// update them without blocking a thread.
			readonly object _stateLock = new object();

			// Unknown until a response says otherwise, and unknown has to mean "do not block": the first request to
			// any route has no headers to go on, and refusing to send it is how you never get any.
			int _remaining = Int32.MaxValue;
			DateTime _resetAt = DateTime.MinValue;

			public Bucket(string key) => _key = key;

			public async Task WaitForSlotAsync(IDiscordClock clock, CancellationToken cancellationToken)
			{
				await _gate.WaitAsync(cancellationToken);
				try
				{
					while (true)
					{
						TimeSpan wait;

						lock (_stateLock)
						{
							DateTime now = clock.UtcNow;

							if (_resetAt != DateTime.MinValue && now >= _resetAt)
							{
								_resetAt = DateTime.MinValue;
								_remaining = Int32.MaxValue;
							}

							if (_remaining > 0)
							{
								_remaining--;
								return;
							}

							// Exhausted with no window to wait for. The two are always set together so this should
							// not arise, but spinning here would stall the notification pipeline - let it through
							// and let the next response re-establish the truth.
							if (_resetAt == DateTime.MinValue)
							{
								_remaining = Int32.MaxValue;
								continue;
							}

							wait = _resetAt - now;
						}

						await clock.DelayAsync(wait, cancellationToken);
					}
				}
				finally
				{
					_gate.Release();
				}
			}

			public void Update(HttpResponseMessage response, DateTime now)
			{
				// A shared-scope 429 is contention with another consumer of the same resource, not our allowance
				// running out. Recording it would throttle us for someone else's traffic.
				if (response.StatusCode == HttpStatusCode.TooManyRequests
					&& String.Equals(GetHeader(response, "X-RateLimit-Scope"), "shared", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}

				bool hasRemaining = TryGetInt(response, "X-RateLimit-Remaining", out int remaining);
				bool hasResetAfter = TryGetSeconds(response, "X-RateLimit-Reset-After", out TimeSpan resetAfter);

				if (!hasRemaining && !hasResetAfter)
				{
					return;
				}

				lock (_stateLock)
				{
					if (hasRemaining)
					{
						_remaining = remaining;
					}

					if (hasResetAfter)
					{
						_resetAt = now + resetAfter;
					}
				}
			}

			public override string ToString() => _key;

			static bool TryGetInt(HttpResponseMessage response, string name, out int value)
				=> Int32.TryParse(GetHeader(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
		}
	}
}
