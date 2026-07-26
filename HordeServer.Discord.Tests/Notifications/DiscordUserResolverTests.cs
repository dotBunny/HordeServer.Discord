// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Acls;
using HordeServer.Discord.Notifications;
using HordeServer.Plugins;
using HordeServer.Users;
using HordeTestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace HordeServer.Discord.Tests.Notifications
{
	/// <summary>
	/// Tests for working out which Discord account belongs to a Horde user.
	/// </summary>
	[TestClass]
	public sealed class DiscordUserResolverTests
	{
		const string AdaEmail = "ada@example.com";
		const string AdaDiscordId = "200000000000000001";
		const string TriageRoleId = "400000000000000001";

		[TestMethod]
		public void AMappedUserResolvesToTheirDiscordAccount()
		{
			DiscordUserResolver resolver = Create(Mapped());

			Assert.AreEqual(AdaDiscordId, resolver.GetUserId(HordeFakes.User("Ada Lovelace", AdaEmail)));
		}

		[TestMethod]
		public void EmailMatchingIgnoresCase()
		{
			DiscordUserResolver resolver = Create(Mapped());

			Assert.AreEqual(AdaDiscordId, resolver.GetUserId(HordeFakes.User("Ada Lovelace", "Ada@Example.COM")),
				"Horde does not normalise the address on an account, and neither should a lookup against it.");
		}

		[TestMethod]
		public void AnUnmappedUserResolvesToNothing()
		{
			DiscordUserResolver resolver = Create(Mapped());

			Assert.IsNull(resolver.GetUserId(HordeFakes.User("Grace Hopper", "grace@example.com")));
		}

		[TestMethod]
		public void AUserWithNoEmailResolvesToNothing()
		{
			DiscordUserResolver resolver = Create(Mapped());

			Assert.IsNull(resolver.GetUserId(HordeFakes.User("Service Account", null)),
				"An email address is the only thing Horde knows that the map can key on.");
		}

		[TestMethod]
		public void AMappingToSomethingThatIsNotASnowflakeIsDiscarded()
		{
			DiscordConfig config = PostLoad(new DiscordConfig
			{
				UserMap = { [AdaEmail] = "@ada" },
			});

			Assert.AreEqual(0, config.ResolvedUsers.Count,
				"A bad entry is dropped so the unmapped path handles it, rather than producing a mention that reaches "
				+ "nobody.");
		}

		[TestMethod]
		public void AKeyThatIsNotAnEmailAddressIsKeptButReported()
		{
			DiscordConfig config = PostLoad(new DiscordConfig
			{
				UserMap = { ["ada"] = AdaDiscordId },
			});

			// Kept on purpose: the check is a shallow guard against a name or a Horde user id in the wrong place, and
			// dropping the entry over it would be a worse outcome than a warning if the guard is ever wrong.
			Assert.AreEqual(1, config.ResolvedUsers.Count);
		}

		[TestMethod]
		public void AMappedAliasResolvesToItsRole()
		{
			DiscordUserResolver resolver = Create(Mapped());

			Assert.AreEqual(TriageRoleId, resolver.GetRole("S0123456789", null)?.RoleId);
			Assert.IsTrue(resolver.IsRoleMapped("S0123456789"));
		}

		[TestMethod]
		public void AnUnmappedAliasResolvesToNothing()
		{
			DiscordUserResolver resolver = Create(Mapped());

			Assert.IsNull(resolver.GetRole("S9999999999", null));
			Assert.IsFalse(resolver.IsRoleMapped("S9999999999"));
		}

		[TestMethod]
		public void AnUnsetAliasIsNotAnUnmappedOne()
		{
			DiscordUserResolver resolver = Create(Mapped());

			Assert.IsNull(resolver.GetRole(null, null));
			Assert.IsNull(resolver.GetRole("   ", null));
		}

		[TestMethod]
		public void ARoleScopedToAGuildIsOnlyUsableThere()
		{
			DiscordConfig config = PostLoad(new DiscordConfig
			{
				Guilds = { ["main"] = "100000000000000001", ["other"] = "100000000000000002" },
				Roles = { ["S0123456789"] = new DiscordRoleMapping { Guild = "main", Role = TriageRoleId } },
			});

			DiscordUserResolver resolver = Create(config);

			Assert.AreEqual(TriageRoleId, resolver.GetRole("S0123456789", "100000000000000001")?.RoleId);
			Assert.IsNull(resolver.GetRole("S0123456789", "100000000000000002"),
				"A role id from another guild renders as raw text and pings nobody, which looks like a formatting "
				+ "bug rather than a configuration one.");
		}

		[TestMethod]
		public void ARoleWithNoGuildIsUsableAnywhere()
		{
			DiscordConfig config = PostLoad(new DiscordConfig
			{
				Guilds = { ["main"] = "100000000000000001" },
				Roles = { ["S0123456789"] = new DiscordRoleMapping { Role = TriageRoleId } },
			});

			DiscordUserResolver resolver = Create(config);

			Assert.IsNotNull(resolver.GetRole("S0123456789", "100000000000000001"));
			Assert.IsNotNull(resolver.GetRole("S0123456789", "100000000000000002"),
				"Unset is right for a single-guild install, where there is nothing to be ambiguous about.");
		}

		[TestMethod]
		public void ARoleNamingAnUnknownGuildIsDropped()
		{
			DiscordConfig config = PostLoad(new DiscordConfig
			{
				Guilds = { ["main"] = "100000000000000001" },
				Roles = { ["S0123456789"] = new DiscordRoleMapping { Guild = "typo", Role = TriageRoleId } },
			});

			Assert.AreEqual(0, config.ResolvedRoles.Count,
				"Treating it as global instead would mention it in guilds it does not belong to.");
		}

		[TestMethod]
		public void BadConfigurationDoesNotThrow()
		{
			DiscordConfig config = PostLoad(new DiscordConfig
			{
				UserMap = { ["ada"] = "nonsense", [AdaEmail] = AdaDiscordId },
				Roles = { ["alias"] = new DiscordRoleMapping { Role = "#triage" } },
			});

			// PostLoad runs inside the server's config reload. Throwing would fail the whole reload and take the
			// other plugins' configuration down with it, over a Discord mapping being wrong.
			Assert.AreEqual(1, config.ResolvedUsers.Count);
			Assert.AreEqual(0, config.ResolvedRoles.Count);
		}

		static DiscordConfig Mapped() => PostLoad(new DiscordConfig
		{
			UserMap = { [AdaEmail] = AdaDiscordId },
			Roles = { ["S0123456789"] = new DiscordRoleMapping { Role = TriageRoleId } },
		});

		static DiscordConfig PostLoad(DiscordConfig config)
		{
			config.PostLoad(new PluginConfigOptions(ConfigVersion.Latest, [], new AclConfig(), NullLogger.Instance));
			return config;
		}

		static DiscordUserResolver Create(DiscordConfig config)
			=> new DiscordUserResolver(new StaticOptionsMonitor<DiscordConfig>(config), NullLogger<DiscordUserResolver>.Instance);
	}
}
