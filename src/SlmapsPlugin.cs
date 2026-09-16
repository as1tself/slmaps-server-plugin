using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using MEC;

[assembly: AssemblyTitle("SlmapsServerPlugin")]
[assembly: AssemblyDescription("Reports the SCP:SL server map seed to slmaps.com so the server can opt out of the seed map site.")]
[assembly: AssemblyVersion(SlmapsServerPlugin.PluginInfo.AssemblyVersion)]
[assembly: AssemblyFileVersion(SlmapsServerPlugin.PluginInfo.AssemblyVersion)]
[assembly: AssemblyInformationalVersion(SlmapsServerPlugin.PluginInfo.Version)]

namespace SlmapsServerPlugin
{
    public sealed class SlmapsPlugin : Plugin<PluginConfig>
    {
        private const double TickIntervalSeconds = 0.5;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private Reporter _reporter;
        private LabApiHost _host;

        internal static Reporter ActiveReporter { get; private set; }
        private CoroutineHandle _tickHandle;
        private bool _subscribed;

        public override string Name { get { return PluginInfo.Name; } }

        public override string Description { get { return "Reports this server's current map seed to slmaps.com so the seed is excluded from the seed map site."; } }

        public override string Author { get { return PluginInfo.Author; } }

        public override Version Version { get { return new Version(PluginInfo.Version); } }

        public override Version RequiredApiVersion { get { return new Version(1, 1, 7); } }

        public override bool IsTransparent { get { return true; } }

        public override void Enable()
        {
            try
            {
                string gameVersion = ReadGameVersion();
                string labApiVersion = ReadLabApiVersion();
                _host = new LabApiHost(this, new CredentialStore(this), gameVersion, labApiVersion);
                Reporter reporter = new Reporter(
                    _host,
                    new HttpApiTransport(PluginInfo.BuildUserAgent(gameVersion, labApiVersion)),
                    MonotonicSeconds,
                    UtcNow);
                _reporter = reporter;
                ActiveReporter = reporter;

                ServerEvents.MapGenerated += OnMapGenerated;
                ServerEvents.RoundStarted += OnRoundStarted;
                ServerEvents.RoundEnded += OnRoundEnded;
                ServerEvents.RoundRestarted += OnRoundRestarted;
                _subscribed = true;

                _host.Info(PluginInfo.Name + " " + PluginInfo.Version + " enabled (port " + _host.Port + ").");
                reporter.Start();
                _tickHandle = Timing.RunCoroutine(TickLoop(reporter));
            }
            catch (Exception ex)
            {
                Logger.Error(PluginInfo.LogPrefix + "Failed to enable: " + ex);
            }
        }

        public override void Disable()
        {
            try
            {
                if (_subscribed)
                {
                    ServerEvents.MapGenerated -= OnMapGenerated;
                    ServerEvents.RoundStarted -= OnRoundStarted;
                    ServerEvents.RoundEnded -= OnRoundEnded;
                    ServerEvents.RoundRestarted -= OnRoundRestarted;
                    _subscribed = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(PluginInfo.LogPrefix + "Failed to unsubscribe events: " + ex.Message);
            }

            try
            {
                Timing.KillCoroutines(_tickHandle);
            }
            catch (Exception ex)
            {
                Logger.Error(PluginInfo.LogPrefix + "Failed to stop the coroutine: " + ex.Message);
            }

            Reporter reporter = _reporter;
            _reporter = null;
            ActiveReporter = null;
            if (reporter != null)
            {
                try
                {
                    reporter.Stop();
                }
                catch (Exception ex)
                {
                    Logger.Error(PluginInfo.LogPrefix + "Failed to stop reporting: " + ex.Message);
                }
            }
        }

        // LabAPI calls this again on a config reload, with a new Config instance.
        public override void LoadConfigs()
        {
            base.LoadConfigs();
            Reporter reporter = _reporter;
            if (reporter != null)
            {
                reporter.NotifyConfigReloaded();
            }
        }

        // Timing.WaitForSeconds counts scaled game time, which an idle server slows to a crawl, so the interval is measured on a Stopwatch.
        private IEnumerator<float> TickLoop(Reporter reporter)
        {
            double nextTickAt = 0;
            while (true)
            {
                double now = MonotonicSeconds();
                if (now >= nextTickAt)
                {
                    nextTickAt = now + TickIntervalSeconds;
                    try
                    {
                        reporter.Tick();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(PluginInfo.LogPrefix + "Tick failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                yield return Timing.WaitForOneFrame;
            }
        }

        private void OnMapGenerated(MapGeneratedEventArgs ev)
        {
            try
            {
                Reporter reporter = _reporter;
                if (reporter == null || ev == null)
                {
                    return;
                }
                int seed = ev.Seed;
                if (seed <= 0)
                {
                    // A canceled generation arrives as -1.
                    _host.Debug("Ignoring MapGenerated with seed " + seed + ".");
                    return;
                }
                reporter.OnMapGenerated(seed);
            }
            catch (Exception ex)
            {
                Logger.Error(PluginInfo.LogPrefix + "MapGenerated handler failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 1.2.1: a forced restart (console "rr") never raises RoundEnded, so the seed's block window stayed open for up
        // to 5 minutes into the next round. Report it as round_end; a restart after a normal RoundEnded is ignored.
        private void OnRoundRestarted()
        {
            try
            {
                Reporter reporter = _reporter;
                if (reporter != null)
                {
                    reporter.OnRoundRestarted();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(PluginInfo.LogPrefix + "RoundRestarted handler failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnRoundStarted()
        {
            try
            {
                Reporter reporter = _reporter;
                if (reporter != null)
                {
                    reporter.OnRoundStarted();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(PluginInfo.LogPrefix + "RoundStarted handler failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnRoundEnded(RoundEndedEventArgs ev)
        {
            try
            {
                Reporter reporter = _reporter;
                if (reporter != null)
                {
                    reporter.OnRoundEnded();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(PluginInfo.LogPrefix + "RoundEnded handler failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static double MonotonicSeconds()
        {
            return Clock.Elapsed.TotalSeconds;
        }

        private static DateTime UtcNow()
        {
            return DateTime.UtcNow;
        }

        // Reading a game member from a separate NoInlining method lets the caller's try/catch survive a game version that dropped it.
        private static string ReadGameVersion()
        {
            try
            {
                return ReadGameVersionUnsafe() ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string ReadLabApiVersion()
        {
            try
            {
                return ReadLabApiVersionUnsafe() ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // VersionString is static readonly, so the running game's value is read instead of a compile-time constant.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string ReadGameVersionUnsafe()
        {
            return GameCore.Version.VersionString;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string ReadLabApiVersionUnsafe()
        {
            Version v = LabApi.Features.LabApiProperties.CurrentVersion;
            return v != null ? v.ToString() : null;
        }
    }
}
