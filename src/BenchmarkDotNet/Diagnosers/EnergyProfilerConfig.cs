using System;
using System.IO;

namespace BenchmarkDotNet.Diagnosers
{
    public class EnergyProfilerConfig
    {
        /// <param name="timeoutInSeconds">How long should we wait for the perfcollect script to finish processing the trace. 300s by default.</param>
        public EnergyProfilerConfig(string metrionBinaryPath, string metrionDatabaseDirectory, string metrionDatabaseNamePattern="monitor_*.db", bool performExtraBenchmarksRun = false, int timeoutInSeconds = 300)
        {
            MetrionBinaryPath = new FileInfo(metrionBinaryPath);
            MetrionDatabaseDirectory = new DirectoryInfo(metrionDatabaseDirectory);
            MetrionDatabaseNamePattern = metrionDatabaseNamePattern;
            RunMode = performExtraBenchmarksRun ? RunMode.ExtraRun : RunMode.NoOverhead;
            Timeout = TimeSpan.FromSeconds(timeoutInSeconds);
        }

        public TimeSpan Timeout { get; }

        public RunMode RunMode { get; }

        public FileInfo MetrionBinaryPath { get; }

        public DirectoryInfo MetrionDatabaseDirectory { get; }
        public string MetrionDatabaseNamePattern { get; }
    }
}
