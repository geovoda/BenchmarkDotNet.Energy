using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Detectors;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Helpers;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Portability;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using JetBrains.Annotations;
using Perfolizer.Horology;

namespace BenchmarkDotNet.Diagnosers
{
    public class EnergyInterval
    {
        public int ProcessId { get; set; }
        public long StartTimestamp { get; set; }
        public long EndTimestamp { get; set; }
        public double EnergyJ    { get; set; }
    }
    public class MetrionEnergyProfiler : IProfiler
    {
        public static readonly IDiagnoser Default = new MetrionEnergyProfiler(new MetrionEnergyProfilerConfig("/home/test/tools/metrion-internal/.venv/bin/metrion", "/home/test/tools/metrion-internal", keepMetrionDatabaseFiles: true));
        private readonly MetrionEnergyProfilerConfig config;
        private readonly Dictionary<int, EnergyInterval> energyIntervals = new();
        private int currentBenchmarkId;
        private Process? metrionProcess;

        [PublicAPI]
        public MetrionEnergyProfiler(MetrionEnergyProfilerConfig config) => this.config = config;
        public IEnumerable<string> Ids => new[] { nameof(MetrionEnergyProfiler) };

        public string ShortName => "metrion";

        public IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();

        public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();

        public RunMode GetRunMode(BenchmarkCase benchmarkCase) => config.RunMode;

        private void AnalyzeMetrionDatabase(DiagnoserActionParameters parameters)
        {
            var logger = parameters.Config.GetCompositeLogger();

            var latestMetrionDbFile = Directory
                .GetFiles(config.MetrionDatabaseDirectory.FullName, config.MetrionDatabaseNamePattern)
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (latestMetrionDbFile == null)
                return;

            string dbPathArg = $"--db-path {latestMetrionDbFile.FullName}";

            if (!energyIntervals.TryGetValue(currentBenchmarkId, out var energyInterval))
                return;

            string pidsArg = $"--filter-pids {energyInterval.ProcessId}";

            string startTime = DateTimeOffset.FromUnixTimeMilliseconds(energyInterval.StartTimestamp).ToString("yyyy-MM-dd HH:mm:ss");
            string endTime = DateTimeOffset.FromUnixTimeMilliseconds(energyInterval.EndTimestamp).ToString("yyyy-MM-dd HH:mm:ss");
            string startTimeArg = $"--start-time \"{startTime}\"";
            string endTimeArg = $"--end-time \"{endTime}\"";

            var processStartInfo = new ProcessStartInfo
            {
                FileName = config.MetrionBinaryPath.FullName,
                Arguments = $"analyze --no-plots --export-summary {startTimeArg} {endTimeArg} {dbPathArg} {pidsArg}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = config.MetrionDatabaseDirectory.FullName,
            };

            logger.WriteLineInfo($"// Execute: {processStartInfo.FileName} {processStartInfo.Arguments} in {processStartInfo.WorkingDirectory}");
            var analyzeProcess = Process.Start(processStartInfo);

            if (analyzeProcess == null)
                return;

            if (!analyzeProcess.WaitForExit(1000 * 10))
            {
                analyzeProcess.KillTree();
            }

            analyzeProcess.Dispose();

            if (config.KeepMetrionDatabaseFiles)
            {
                var traceFilePath = new FileInfo(ArtifactFileNameHelper.GetTraceFilePath(parameters, latestMetrionDbFile.CreationTime, $"pid{energyInterval.ProcessId}.db"));
                File.Move(latestMetrionDbFile.FullName, traceFilePath.FullName);
            }

            energyIntervals[currentBenchmarkId].EnergyJ = ExtractLatestMetrionEnergyMeasurement(energyInterval.ProcessId);
        }

        private double ExtractLatestMetrionEnergyMeasurement(int processId)
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

            var jsonFile = File.ReadAllText(latestMetrionOutputFile.FullName);
            if (!jsonFile.Contains($"[{processId}]"))
            {
                return 0;
            }

            using var jsonDocument = JsonDocument.Parse(jsonFile);
            var rootElement = jsonDocument.RootElement;

            double totalEnergy = rootElement
                .GetProperty("summary")
                .GetProperty("total_energy")
                .GetDouble();

            return totalEnergy;
        }

        public IEnumerable<Metric> ProcessResults(DiagnoserResults results) => Array.Empty<Metric>();

        public void DisplayResults(ILogger logger)
        {
            if (!energyIntervals.TryGetValue(currentBenchmarkId, out var energyInterval))
            {
                return;
            }

            logger.WriteLineInfo($"Total energy measured with Metrion: {energyInterval.EnergyJ} J");
        }

        public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
        {
            currentBenchmarkId = parameters.BenchmarkId.GetHashCode();
            energyIntervals.TryGetValue(currentBenchmarkId, out var energyInterval);

            if (energyInterval == null)
                energyInterval = new EnergyInterval();

            switch (signal)
            {
                case HostSignal.BeforeAnythingElse:
                    StartMetrion(parameters);
                    break;
                case HostSignal.AfterProcessStart:
                    energyInterval.ProcessId = parameters.Process.Id;
                    break;
                case HostSignal.BeforeActualRun:
                    energyInterval.StartTimestamp = Chronometer.GetTimestamp() / 1_000_000;
                    break;
                case HostSignal.AfterActualRun:
                    energyInterval.EndTimestamp = Chronometer.GetTimestamp() / 1_000_000;
                    break;
                case HostSignal.AfterAll:
                    StopMetrion(parameters);
                    AnalyzeMetrionDatabase(parameters);
                    break;
            }

            energyIntervals.Remove(currentBenchmarkId);
            energyIntervals.Add(currentBenchmarkId, energyInterval);
        }

        public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
        {
            if (!OsDetector.IsLinux())
            {
                yield return new ValidationError(true, "The EnergyProfiler works only on Linux!");
            }
            if (!config.MetrionBinaryPath.Exists)
            {
                yield return new ValidationError(true, "Metrion Binary not found!");
            }
        }

        private bool StartMetrion(DiagnoserActionParameters parameters)
        {
            var logger = parameters.Config.GetCompositeLogger();

            var processStartInfo = new ProcessStartInfo
            {
                FileName = config.MetrionBinaryPath.FullName,
                Arguments = $"monitor",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = config.MetrionBinaryPath.Directory.FullName,
            };

            logger.WriteLineInfo( $"// Execute: {processStartInfo.FileName} {processStartInfo.Arguments} in {processStartInfo.WorkingDirectory}");
            metrionProcess = Process.Start(processStartInfo);

            if (metrionProcess == null)
                return false;

            return true;
        }

        private void StopMetrion(DiagnoserActionParameters parameters)
        {
            var logger = parameters.Config.GetCompositeLogger();

            if (metrionProcess == null)
                return;

            try
            {

                if (!metrionProcess.HasExited)
                {
                    if (libc.kill(metrionProcess.Id, libc.Signals.SIGINT) != 0)
                    {
                        int lastError = Marshal.GetLastWin32Error();
                        logger.WriteLineError($"kill(metrion, SIGINT) failed with {lastError}");
                    }

                    if (!metrionProcess.WaitForExit((int)config.Timeout.TotalMilliseconds))
                    {
                        logger.WriteLineError($"The Metrion script did not stop in {config.Timeout.TotalSeconds}s. It's going to be force killed now.");
                        metrionProcess.KillTree(); // kill the entire process tree
                    }
                }
                else
                {
                    logger.WriteLineError("For some reason the metrion script has finished sooner than expected.");
                }
            }
            finally
            {
                metrionProcess.Dispose();
            }
        }
    }
}