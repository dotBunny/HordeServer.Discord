// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace DiscordSmoke
{
	/// <summary>
	/// Logger provider that remembers what the plugin complained about, so a scenario can be judged on it.
	/// </summary>
	/// <remarks>
	/// <see cref="HordeServer.Discord.Client.DiscordClient"/> logs a failed request and returns; it does not throw.
	/// That is deliberate and must stay that way - inside a real server a sink that throws is a sink that disturbs
	/// the other sinks - but it means a scenario that Discord rejected outright still returns normally, and the tool
	/// reported every one of them as sent. Watching the log is the only signal there is.
	///
	/// Recording rather than printing also fixes the ordering. The console logger writes on its own thread, so
	/// errors landed under whichever scenario line happened to be current, and the last one arrived after the
	/// summary. Here <see cref="Program"/> prints them itself, under the scenario that caused them.
	/// </remarks>
	sealed class SmokeLog : ILoggerProvider
	{
		// Discord puts a numeric code in the error body alongside the HTTP status, and it is the code that says
		// what to go and fix - 403 alone does not distinguish "not invited" from "cannot DM that person".
		static readonly Regex s_errorCode = new Regex(@"""code"":\s*(\d+)", RegexOptions.Compiled);

		readonly object _lock = new object();
		readonly List<string> _problems = new List<string>();
		readonly SortedSet<int> _codes = new SortedSet<int>();

		/// <summary>
		/// What has been logged at warning or above since the last <see cref="Clear"/>.
		/// </summary>
		public IReadOnlyList<string> Problems
		{
			get
			{
				lock (_lock)
				{
					return _problems.ToArray();
				}
			}
		}

		/// <summary>
		/// Every Discord error code seen across the whole run, for the closing diagnosis.
		/// </summary>
		/// <remarks>
		/// Deliberately not cleared by <see cref="Clear"/>. The same misconfiguration fails most of the scenarios,
		/// so the useful summary is the set of distinct causes rather than one per message.
		/// </remarks>
		public IReadOnlyCollection<int> SeenCodes
		{
			get
			{
				lock (_lock)
				{
					return _codes.ToArray();
				}
			}
		}

		/// <summary>
		/// Forgets the recorded problems, before running the next scenario.
		/// </summary>
		public void Clear()
		{
			lock (_lock)
			{
				_problems.Clear();
			}
		}

		public ILogger CreateLogger(string categoryName) => new Recorder(this);

		public void Dispose()
		{
		}

		void Record(string message)
		{
			lock (_lock)
			{
				_problems.Add(message);

				foreach (Match match in s_errorCode.Matches(message))
				{
					if (Int32.TryParse(match.Groups[1].Value, out int code))
					{
						_codes.Add(code);
					}
				}
			}
		}

		sealed class Recorder : ILogger
		{
			readonly SmokeLog _owner;

			public Recorder(SmokeLog owner)
			{
				_owner = owner;
			}

			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			// Warnings and errors are the whole point; anything below is noise against fifteen scenarios.
			public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				if (!IsEnabled(logLevel))
				{
					return;
				}

				string message = formatter(state, exception);
				_owner.Record(exception == null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})");
			}
		}
	}
}
