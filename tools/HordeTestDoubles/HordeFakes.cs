// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using EpicGames.Core;
using EpicGames.Horde.Jobs;
using EpicGames.Horde.Jobs.Bisect;
using EpicGames.Horde.Streams;
using EpicGames.Horde.Users;
using HordeServer;
using HordeServer.Jobs.TestData;
using HordeServer.Users;
using HordeServer.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace HordeTestDoubles
{
	/// <summary>
	/// Stand-ins for the parts of Horde a notification arrives with.
	/// </summary>
	/// <remarks>
	/// Hand-written rather than mocked. There is no mocking package in this project and adding one to test four
	/// property bags would be a poor trade - these are all data, and the members nobody reads throw rather than
	/// returning a lie that a later test might come to depend on.
	/// </remarks>
	public static class HordeFakes
	{
		/// <summary>
		/// A user with a name worth reading in a message.
		/// </summary>
		public static IUser User(string name, string? email = null) => new FakeUser(name, email);
	}

	public sealed class FakeUser : IUser
	{
		static int s_next;

		public FakeUser(string name, string? email)
		{
			Name = name;
			Email = email;
			Login = name.Replace(" ", ".", StringComparison.Ordinal).ToLowerInvariant();

			// Ids are counted rather than random so a failing assertion reads the same on every run.
			Id = UserId.Parse(Interlocked.Increment(ref s_next).ToString("x24", null));
		}

		public UserId Id { get; }

		public string Name { get; }

		public string Login { get; }

		public string? Email { get; }
	}

	/// <summary>
	/// A user collection holding a fixed set of users.
	/// </summary>
	public sealed class FakeUserCollection : IUserCollection
	{
		readonly Dictionary<UserId, IUser> _users = new Dictionary<UserId, IUser>();

		/// <summary>
		/// Adds a user and returns the id it was filed under.
		/// </summary>
		public UserId Add(IUser user)
		{
			_users[user.Id] = user;
			return user.Id;
		}

		public Task<IUser?> GetUserAsync(UserId id, CancellationToken cancellationToken = default)
			=> Task.FromResult(_users.GetValueOrDefault(id));

		public ValueTask<IUser?> GetCachedUserAsync(UserId? id, CancellationToken cancellationToken = default)
			=> new ValueTask<IUser?>(id == null ? null : _users.GetValueOrDefault(id.Value));

		public Task<IReadOnlyList<IUser>> FindUsersAsync(IEnumerable<UserId>? ids = null, string? nameRegex = null, int? index = null, int? count = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<IUser?> FindUserByLoginAsync(string login, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		// Read by issue triage: an interaction identifies its author by Discord snowflake, the user map turns that
		// into an email, and this turns the email into the Horde user every issue operation is audited against.
		public Task<IUser?> FindUserByEmailAsync(string email, CancellationToken cancellationToken = default)
			=> Task.FromResult(_users.Values.FirstOrDefault(x
				=> String.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)));

		public Task<IUser> FindOrAddUserByLoginAsync(string login, string? name = null, string? email = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<IUserClaims> GetClaimsAsync(UserId userId, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task UpdateClaimsAsync(UserId userId, IEnumerable<IUserClaim> claims, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<IUserSettings> GetSettingsAsync(UserId userId, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task UpdateSettingsAsync(UserId userId, bool? enableExperimentalFeatures = null, bool? alwaysTagPreflightCL = null, BsonValue? dashboardSettings = null, IEnumerable<JobId>? addPinnedJobIds = null, IEnumerable<JobId>? removePinnedJobIds = null, UpdateUserJobTemplateOptions? templateOptions = null, IEnumerable<BisectTaskId>? addBisectTaskIds = null, IEnumerable<BisectTaskId>? removeBisectTaskIds = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();
	}

	/// <summary>
	/// Server information carrying only the dashboard URL, which is all the notification formatting reads.
	/// </summary>
	public sealed class FakeServerInfo : IServerInfo
	{
		public Uri DashboardUrl { get; set; } = new Uri("https://horde.example.com/");

		public SemVer Version => throw new NotSupportedException();

		public string Environment => throw new NotSupportedException();

		public string SessionId => throw new NotSupportedException();

		public DirectoryReference AppDir => throw new NotSupportedException();

		public DirectoryReference DataDir => throw new NotSupportedException();

		public IConfiguration Configuration => throw new NotSupportedException();

		public bool ReadOnlyMode => throw new NotSupportedException();

		public bool EnableDebugEndpoints => throw new NotSupportedException();

		public Uri ServerUrl => throw new NotSupportedException();

		public bool IsRunModeActive(RunMode mode) => throw new NotSupportedException();
	}

	/// <summary>
	/// A test health report with everything the notification reads set directly.
	/// </summary>
	public sealed class FakeTestHealthReport : ITestHealthReport
	{
		public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

		public TestId TestId { get; set; } = TestId.Parse("000000000000000000000001");

		public string TestName { get; set; } = "Project.Boot";

		public StreamId StreamId { get; set; } = new StreamId("dethol-main");

		public DateTime LastUpdateDateUtc { get; set; } = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

		public bool IsHealthy { get; set; }

		public string State { get; set; } = "Unreliable";

		public string? PreviousState { get; set; }

		public int SuccessRate { get; set; } = 40;

		public int FailureRate { get; set; } = 55;

		public int CatastrophicFailureRate { get; set; }

		public int RedundantErrorRate { get; set; }

		public DateTime? NotificationLastDateUtc { get; set; }
	}

	/// <summary>
	/// An options monitor over a value that never changes.
	/// </summary>
	public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
	{
		public StaticOptionsMonitor(T value) => CurrentValue = value;

		public T CurrentValue { get; }

		public T Get(string? name) => CurrentValue;

		public IDisposable OnChange(Action<T, string?> listener) => NullChangeToken.Disposable;

		sealed class NullChangeToken : IDisposable
		{
			public static IDisposable Disposable { get; } = new NullChangeToken();

			public void Dispose()
			{ }
		}
	}
}
