using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Detectors;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Portability;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using JetBrains.Annotations;
using Perfolizer.Horology;

namespace BenchmarkDotNet.Diagnosers
{
    public class EnergyTimestamp
    {
        public int ProcessId { get; set; }
        public long StartTimestamp { get; set; }
        public long EndTimestamp { get; set; }
        public double EnergyJ    { get; set; }

        public EnergyTimestamp() { }
    }
    public class EnergyProfiler : IProfiler
    {
        public static readonly IDiagnoser Default = new EnergyProfiler(new EnergyProfilerConfig("/home/test/tools/metrion-internal/.venv/bin/metrion", "/home/test/tools/metrion-internal"));
        private readonly EnergyProfilerConfig config;
        private readonly Dictionary<BenchmarkId, EnergyTimestamp> energyTimestamps = new Dictionary<BenchmarkId, EnergyTimestamp>();
        private Process metrionProcess;

        [PublicAPI]
        public EnergyProfiler(EnergyProfilerConfig config) => this.config = config;
        public IEnumerable<string> Ids => new[] { nameof(EnergyProfiler) };

        public string ShortName => "metrion";

        public IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();

        public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();

        public RunMode GetRunMode(BenchmarkCase benchmarkCase) => config.RunMode;

        public void AnalyzeMetrionDatabase()
        {
            var latestMetrionDbFile = Directory
                .GetFiles(config.MetrionDatabaseDirectory.FullName, config.MetrionDatabaseNamePattern)
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (latestMetrionDbFile == null)
                return;

            string dbPathArg = $"--db-path {latestMetrionDbFile.FullName}";

            foreach (var key in energyTimestamps.Keys.ToList())
            {
                var timestamp = energyTimestamps[key];
                string pidsArg = $"--filter-pids {timestamp.ProcessId}";

                string startTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.StartTimestamp).ToString("yyyy-MM-dd HH:mm:ss");
                string endTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.EndTimestamp).ToString("yyyy-MM-dd HH:mm:ss");

                string startTimeArg = $"--start-time \"{startTime}\"";
                string endTimeArg = $"--end-time \"{endTime}\"";

                var start = new ProcessStartInfo
                {
                    FileName = config.MetrionBinaryPath.FullName,
                    Arguments = $"analyze --no-plots --export-summary {startTimeArg} {endTimeArg} {dbPathArg} {pidsArg}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = config.MetrionDatabaseDirectory.FullName,
                };

                metrionProcess = Process.Start(start);

                if (!metrionProcess.WaitForExit(1000 * 10))
                {
                    metrionProcess.KillTree();
                }

                metrionProcess.Dispose();

                energyTimestamps[key].EnergyJ = ExtractLatestMetrionEnergyMeasurement(timestamp.ProcessId);
            }
        }

        public double ExtractLatestMetrionEnergyMeasurement(int processId)
        {
            DirectoryInfo measurementsDirectory = new DirectoryInfo(Path.Combine(config.MetrionDatabaseDirectory.FullName, "metrion/energy_attribution/output/"));

            if (!measurementsDirectory.Exists)
            {
                return 0;
            }

            var latestMetrionOutputFile = Directory
                .GetFiles(measurementsDirectory.FullName, "*.json")
                .Select(path => new FileInfo(path))
                .FirstOrDefault();

            if (latestMetrionOutputFile == null)
            {
                return 0;
            }

            var json = File.ReadAllText(latestMetrionOutputFile.FullName);
            if (!json.Contains($"[{processId}]"))
            {
                return 0;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double totalEnergy = root
                .GetProperty("summary")
                .GetProperty("total_energy")
                .GetDouble();

            return totalEnergy;
        }

        public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
        {
            StopMetrion();
            AnalyzeMetrionDatabase();

            yield break;
        }

        public void DisplayResults(ILogger logger)
        {
            // if (!benchmarkToTraceFile.Any())
            //     return;
            //
            // logger.WriteLineInfo($"Exported {benchmarkToTraceFile.Count} trace file(s). Example:");
            // logger.WriteLineInfo(benchmarkToTraceFile.Values.First().FullName);
        }

        public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
        {
            energyTimestamps.TryGetValue(parameters.BenchmarkId, out var timestamp);

            if (timestamp == null)
                timestamp = new EnergyTimestamp();

            switch (signal)
            {
                case HostSignal.AfterProcessStart:
                    timestamp.ProcessId = parameters.Process?.Id ?? 0;
                    break;
                case HostSignal.BeforeActualRun:
                    timestamp.StartTimestamp = Chronometer.GetTimestamp();
                    break;
                case HostSignal.AfterActualRun:
                    timestamp.EndTimestamp = Chronometer.GetTimestamp();
                    break;
            }

            energyTimestamps.Remove(parameters.BenchmarkId);
            energyTimestamps.Add(parameters.BenchmarkId, timestamp);
        }

        public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
        {
            if (!OsDetector.IsLinux())
            {
                yield return new ValidationError(true, "The EnergyProfiler works only on Linux!");
            }
            if (!TryStartMetrion(validationParameters))
            {
                yield return new ValidationError(true, "Failed to start Metrion. Make sure Metrion is installed and configured correctly.");
            }
        }

        private bool TryStartMetrion(ValidationParameters validationParameters)
        {
            if (!config.MetrionBinaryPath.Exists)
            {
                return false;
            }

            var start = new ProcessStartInfo
            {
                FileName = config.MetrionBinaryPath.FullName,
                Arguments = $"monitor",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = config.MetrionBinaryPath.Directory.FullName,
            };

            metrionProcess = Process.Start(start);

            if (metrionProcess == null)
                return false;

            return true;
        }

        private void StopMetrion()
        {
            try
            {
                if (!metrionProcess.HasExited)
                {
                    if (libc.kill(metrionProcess.Id, libc.Signals.SIGINT) != 0)
                    {
                        //logger.WriteLineError($"");
                    }

                    if (!metrionProcess.WaitForExit((int)config.Timeout.TotalMilliseconds))
                    {
                        //logger.WriteLineError($"");

                        metrionProcess.KillTree(); // kill the entire process tree
                    }
                }
                else
                {
                    //logger.WriteLineError("For some reason the metrion script has finished sooner than expected.");
                }
            }
            finally
            {
                metrionProcess.Dispose();
            }
        }
    }
}