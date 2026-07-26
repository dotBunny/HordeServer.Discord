// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Notifications;
using HordeServer.Discord.Tests.Client;

namespace HordeServer.Discord.Tests.Notifications
{
	/// <summary>
	/// Tests for suppressing repeat announcements of a condition that has not changed.
	/// </summary>
	[TestClass]
	public sealed class DiscordRepeatFilterTests
	{
		const string EventId = "config-update";

		[TestMethod]
		public void FirstReportOfAnEventIsNews()
		{
			DiscordRepeatFilter filter = new DiscordRepeatFilter(new FakeDiscordClock());

			Assert.IsTrue(filter.RecordIfChanged(EventId, "broken"));
		}

		[TestMethod]
		public void RepeatingTheSameStateIsNot()
		{
			DiscordRepeatFilter filter = new DiscordRepeatFilter(new FakeDiscordClock());

			filter.RecordIfChanged(EventId, "broken");

			Assert.IsFalse(filter.RecordIfChanged(EventId, "broken"),
				"Horde re-reads its configuration on a ticker, so an unchanged failure arrives over and over. "
				+ "Posting each one fills the channel with the same message.");
		}

		[TestMethod]
		public void ADifferentFailureIsWorthSayingAgain()
		{
			DiscordRepeatFilter filter = new DiscordRepeatFilter(new FakeDiscordClock());

			filter.RecordIfChanged(EventId, "missing brace in streams.json");

			Assert.IsTrue(filter.RecordIfChanged(EventId, "unknown property in projects.json"),
				"Somebody fixed one thing and broke another; that is new information.");
		}

		[TestMethod]
		public void EventsDoNotInterfereWithEachOther()
		{
			DiscordRepeatFilter filter = new DiscordRepeatFilter(new FakeDiscordClock());

			filter.RecordIfChanged("test-health-a", "degraded");

			Assert.IsTrue(filter.RecordIfChanged("test-health-b", "degraded"));
		}

		[TestMethod]
		public void AnUnchangedStateBecomesNewsAgainOnceItHasAged()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRepeatFilter filter = new DiscordRepeatFilter(clock);

			filter.RecordIfChanged(EventId, "broken");
			clock.Advance(DiscordRepeatFilter.Lifetime + TimeSpan.FromMinutes(1.0));

			Assert.IsTrue(filter.RecordIfChanged(EventId, "broken"),
				"Suppression is meant to stop a burst of identical messages, not to stay silent about a farm that "
				+ "has been broken for a week.");
		}

		[TestMethod]
		public void ClearingReportsWhetherAnythingHadBeenSaid()
		{
			DiscordRepeatFilter filter = new DiscordRepeatFilter(new FakeDiscordClock());

			Assert.IsFalse(filter.Clear(EventId),
				"A recovery message is noise in a channel that was never told about the problem.");

			filter.RecordIfChanged(EventId, "broken");

			Assert.IsTrue(filter.Clear(EventId));
			Assert.IsFalse(filter.Clear(EventId), "Clearing twice must not announce recovery twice.");
		}

		[TestMethod]
		public void ClearingAnExpiredEventIsNotARecovery()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRepeatFilter filter = new DiscordRepeatFilter(clock);

			filter.RecordIfChanged(EventId, "broken");
			clock.Advance(DiscordRepeatFilter.Lifetime + TimeSpan.FromMinutes(1.0));

			Assert.IsFalse(filter.Clear(EventId),
				"Nobody is still watching for the resolution of something announced a week ago.");
		}

		[TestMethod]
		public void RecordingWithoutStateStillArmsTheRecoveryMessage()
		{
			DiscordRepeatFilter filter = new DiscordRepeatFilter(new FakeDiscordClock());

			filter.Record("test-health-a");

			Assert.IsTrue(filter.Clear("test-health-a"));
		}

		[TestMethod]
		public void TheStoreStaysBounded()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRepeatFilter filter = new DiscordRepeatFilter(clock);

			// The event id space is not bounded - test health is keyed per test - so a long-running server must not
			// accumulate an entry per test it has ever seen.
			for (int index = 0; index < DiscordRepeatFilter.Capacity * 2; index++)
			{
				filter.Record($"test-health-{index}");
				clock.Advance(TimeSpan.FromSeconds(1.0));
			}

			Assert.IsTrue(filter.Count <= DiscordRepeatFilter.Capacity + 1,
				$"{filter.Count} entries were kept, over a capacity of {DiscordRepeatFilter.Capacity}.");
		}

		[TestMethod]
		public void TheOldestEntriesAreTheOnesDiscarded()
		{
			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordRepeatFilter filter = new DiscordRepeatFilter(clock);

			filter.Record("first");

			for (int index = 0; index < DiscordRepeatFilter.Capacity * 2; index++)
			{
				clock.Advance(TimeSpan.FromSeconds(1.0));
				filter.Record($"test-health-{index}");
			}

			Assert.IsFalse(filter.Clear("first"), "Eviction should take the least recently reported event.");
			Assert.IsTrue(filter.Clear($"test-health-{(DiscordRepeatFilter.Capacity * 2) - 1}"),
				"The most recent event must survive eviction, or a recovery message would never pair up.");
		}
	}
}
