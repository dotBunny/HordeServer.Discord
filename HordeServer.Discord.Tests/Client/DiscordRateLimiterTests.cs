// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Net;
using HordeServer.Discord.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// Tests for the rate limiter's decisions - what it waits for and how long - with the waiting itself faked out.
	/// </summary>
	[TestClass]
	public sealed class DiscordRateLimiterTests
	{
		static readonly DiscordRoute s_channelA = DiscordRoute.CreateMessage("111");
		static readonly DiscordRoute s_channelB = DiscordRoute.CreateMessage("222");

		[TestMethod]
		public async Task FirstRequestToAnUnknownRouteIsNotDelayed()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(Ok());

			using HttpResponseMessage response = await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None);

			Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
			Assert.AreEqual(0, clock.Delays.Count,
				"Nothing is known about a route until a response comes back, and refusing to send the first request "
				+ "is how you never find out.");
		}

		[TestMethod]
		public async Task ExhaustedBucketWaitsForItsWindowToReset()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(
				Ok(("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset-After", "5")),
				Ok(("X-RateLimit-Remaining", "4"), ("X-RateLimit-Reset-After", "5")));

			(await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None)).Dispose();
			(await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None)).Dispose();

			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5.0) }, clock.Delays,
				"The second request should have waited out the window the first response reported as exhausted.");
		}

		[TestMethod]
		public async Task ChannelsDoNotShareABucket()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(
				Ok(("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset-After", "30")),
				Ok());

			(await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None)).Dispose();
			(await limiter.SendAsync(s_channelB, responses.SendAsync, CancellationToken.None)).Dispose();

			Assert.AreEqual(0, clock.Delays.Count,
				"Channel id is a major parameter, so exhausting one channel's bucket must not throttle another.");
			Assert.AreEqual(2, limiter.TrackedBucketCount);
		}

		[TestMethod]
		public async Task ThrottledRequestIsRetriedAfterTheReportedDelay()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(
				Throttled(("X-RateLimit-Reset-After", "2"), ("X-RateLimit-Scope", "user")),
				Ok());

			using HttpResponseMessage response = await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None);

			Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
			Assert.AreEqual(2, responses.SendCount);
			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(2.0) }, clock.Delays);
		}

		[TestMethod]
		public async Task FractionalRetryAfterIsHonoured()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(Throttled(("X-RateLimit-Reset-After", "0.75")), Ok());

			(await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None)).Dispose();

			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(0.75) }, clock.Delays,
				"Reset-After is fractional seconds; rounding it up would waste most of a second on every throttle.");
		}

		[TestMethod]
		public async Task SharedScopeThrottleDoesNotPoisonOurBucket()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(
				Throttled(("X-RateLimit-Scope", "shared"), ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset-After", "3")),
				Ok(),
				Ok());

			(await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None)).Dispose();
			(await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None)).Dispose();

			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3.0) }, clock.Delays,
				"The retry itself has to wait, but a shared-scope 429 is someone else's contention - recording "
				+ "Remaining=0 against our bucket would have made the follow-up request wait a second time.");
		}

		[TestMethod]
		public async Task GlobalCeilingThrottlesABurst()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock, globalRequestsPerSecond: 2);
			ResponseSequence responses = new ResponseSequence(Ok(), Ok(), Ok());

			for (int idx = 0; idx < 3; idx++)
			{
				(await limiter.SendAsync(DiscordRoute.CreateMessage(idx.ToString()), responses.SendAsync, CancellationToken.None)).Dispose();
			}

			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1.0) }, clock.Delays,
				"The global ceiling is per token, not per route, so three requests across three channels still "
				+ "have to fit inside it.");
		}

		[TestMethod]
		public async Task InteractionRoutesIgnoreTheGlobalCeiling()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock, globalRequestsPerSecond: 2);
			ResponseSequence responses = new ResponseSequence(Ok(), Ok(), Ok());

			for (int idx = 0; idx < 3; idx++)
			{
				(await limiter.SendAsync(DiscordRoute.InteractionCallback(idx.ToString()), responses.SendAsync, CancellationToken.None)).Dispose();
			}

			Assert.AreEqual(0, clock.Delays.Count,
				"Interaction endpoints are exempt from the global limit, which is what keeps triage buttons "
				+ "responsive while a broken stream saturates the notification path.");
		}

		[TestMethod]
		public async Task GlobalThrottleOnOneRouteStallsTheOthers()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(
				Throttled(("X-RateLimit-Scope", "global"), ("X-RateLimit-Reset-After", "4")),
				Ok(),
				Ok());

			(await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None)).Dispose();
			(await limiter.SendAsync(s_channelB, responses.SendAsync, CancellationToken.None)).Dispose();

			// The retry waits out the four seconds, which also satisfies the cooldown by the time channel B is
			// asked for - so exactly one delay, not two.
			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(4.0) }, clock.Delays);
		}

		[TestMethod]
		public async Task GlobalCooldownIsWaitedOutByAnUnrelatedRoute()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock, maxAttempts: 1);
			ResponseSequence responses = new ResponseSequence(
				Throttled(("X-RateLimit-Scope", "global"), ("X-RateLimit-Reset-After", "4")),
				Ok());

			// maxAttempts of 1 means the throttled response comes straight back without the limiter waiting, so the
			// cooldown it recorded is still outstanding when the next request arrives.
			(await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None)).Dispose();
			Assert.AreEqual(0, clock.Delays.Count);

			(await limiter.SendAsync(s_channelB, responses.SendAsync, CancellationToken.None)).Dispose();
			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(4.0) }, clock.Delays);
		}

		[TestMethod]
		public async Task RetryAfterBeyondTheCapIsReportedRatherThanWaitedOut()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(Throttled(("X-RateLimit-Reset-After", "3600")), Ok());

			using HttpResponseMessage response = await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None);

			Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
			Assert.AreEqual(1, responses.SendCount);
			Assert.AreEqual(0, clock.Delays.Count,
				"An hour-long ban is not something to block a notification on; hand it back and let the caller log it.");
		}

		[TestMethod]
		public async Task PersistentThrottlingGivesUpAndReturnsTheLastResponse()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock, maxAttempts: 3);
			ResponseSequence responses = new ResponseSequence(
				Throttled(("X-RateLimit-Reset-After", "1")),
				Throttled(("X-RateLimit-Reset-After", "1")),
				Throttled(("X-RateLimit-Reset-After", "1")),
				Ok());

			using HttpResponseMessage response = await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None);

			Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
			Assert.AreEqual(3, responses.SendCount, "Three attempts, then the failure belongs to the caller.");
		}

		[TestMethod]
		public async Task MissingHeadersFallBackToAShortRetry()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(Throttled(), Ok());

			(await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None)).Dispose();

			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1.0) }, clock.Delays);
		}

		[TestMethod]
		public async Task NonThrottledFailuresAreReturnedUntouched()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRateLimiter limiter = Create(clock);
			ResponseSequence responses = new ResponseSequence(new HttpResponseMessage(HttpStatusCode.Forbidden));

			using HttpResponseMessage response = await limiter.SendAsync(s_channelA, responses.SendAsync, CancellationToken.None);

			Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
			Assert.AreEqual(1, responses.SendCount,
				"A misconfigured channel or a missing permission will not fix itself; retrying just delays the error.");
		}

		static DiscordRateLimiter Create(IDiscordClock clock, int globalRequestsPerSecond = DiscordRateLimiter.DefaultGlobalRequestsPerSecond, int maxAttempts = DiscordRateLimiter.DefaultMaxAttempts)
			=> new DiscordRateLimiter(NullLogger.Instance, clock, globalRequestsPerSecond, maxAttempts);

		static HttpResponseMessage Ok(params (string Name, string Value)[] headers)
			=> WithHeaders(new HttpResponseMessage(HttpStatusCode.OK), headers);

		static HttpResponseMessage Throttled(params (string Name, string Value)[] headers)
			=> WithHeaders(new HttpResponseMessage(HttpStatusCode.TooManyRequests), headers);

		static HttpResponseMessage WithHeaders(HttpResponseMessage response, (string Name, string Value)[] headers)
		{
			foreach ((string name, string value) in headers)
			{
				response.Headers.TryAddWithoutValidation(name, value);
			}

			return response;
		}

		/// <summary>
		/// Hands back canned responses in order, then keeps returning the last one.
		/// </summary>
		sealed class ResponseSequence
		{
			readonly Queue<HttpResponseMessage> _responses;

			public ResponseSequence(params HttpResponseMessage[] responses)
				=> _responses = new Queue<HttpResponseMessage>(responses);

			public int SendCount { get; private set; }

			public Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken)
			{
				SendCount++;

				return Task.FromResult(_responses.Count > 0
					? _responses.Dequeue()
					: new HttpResponseMessage(HttpStatusCode.OK));
			}
		}
	}
}
