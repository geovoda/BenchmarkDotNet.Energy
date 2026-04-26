using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
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
        public DateTime StartTimestamp { get; set; }
        public DateTime EndTimestamp { get; set; }
        public double EnergyJ    { get; set; }
    }
    public class MetrionEnergyProfiler : IProfiler
    {
        public static readonly IDiagnoser Default = new MetrionEnergyProfiler(new MetrionEnergyProfilerConfig("/home/test/tools/metrion-internal/.venv/bin/metrion", "/home/test/tools/metrion-internal", keepMetrionDatabaseFiles: true));
        private readonly MetrionEnergyProfilerConfig config;
        private readonly Dictionary<BenchmarkCase, EnergyInterval> energyIntervals = new();
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
            logger.WriteLineInfo($"Analyzing latest Metrion database file.");

            var latestMetrionDbFile = Directory
                .GetFiles(config.MetrionDatabaseDirectory.FullName, config.MetrionDatabaseNamePattern)
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (latestMetrionDbFile == null)
            {
                logger.WriteLineError($"Metrion latest database file not found: {config.MetrionDatabaseDirectory.FullName}");
                return;
            }

            string dbPathArg = $"--db-path {latestMetrionDbFile.FullName}";

            if (!energyIntervals.TryGetValue(parameters.BenchmarkCase, out var energyInterval))
            {
                logger.WriteLineError($"Metrion benchmark information not found.");
                return;
            }

            string pidsArg = $"--filter-pids {energyInterval.ProcessId}";

            string startTime = energyInterval.StartTimestamp.ToString("yyyy-MM-dd HH:mm:ss");
            string endTime = energyInterval.EndTimestamp.ToString("yyyy-MM-dd HH:mm:ss");
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

            energyIntervals[parameters.BenchmarkCase].EnergyJ = ExtractLatestMetrionEnergyMeasurement(logger, energyInterval.ProcessId);

            if (config.KeepMetrionDatabaseFiles)
            {
                var traceFilePath = new FileInfo(ArtifactFileNameHelper.GetTraceFilePath(parameters, latestMetrionDbFile.CreationTime, $"pid{energyInterval.ProcessId}.db"));
                File.Move(latestMetrionDbFile.FullName, traceFilePath.FullName);
            }
        }

        private double ExtractLatestMetrionEnergyMeasurement(ILogger logger, int processId)
        {
            DirectoryInfo measurementsDirectory = new DirectoryInfo(Path.Combine(config.MetrionDatabaseDirectory.FullName, "metrion/energy_attribution/output/"));

            if (!measurementsDirectory.Exists)
            {
                logger.WriteLineError($"Unable to find measurements directory: {measurementsDirectory.FullName}");
                return 0;
            }

            var latestMetrionOutputFile = Directory
                .GetFiles(measurementsDirectory.FullName, "*.json")
                .Select(path => new FileInfo(path))
                .FirstOrDefault();

            if (latestMetrionOutputFile == null)
            {
                logger.WriteLineError($"The measurements files were not found in the directory {measurementsDirectory.FullName}");
                return 0;
            }

            var jsonFile = File.ReadAllText(latestMetrionOutputFile.FullName);
            if (!jsonFile.Contains($"[{processId}]"))
            {
                logger.WriteLineError($"The latest measurement file contains a different processId.");
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

        public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
        {
            if (!energyIntervals.TryGetValue(results.BenchmarkCase, out var energyInterval))
            {
                yield return new Metric(EnergyMetricDescriptor.AverageMetrionCpuEnergyPerOperation, double.NaN);
                yield return new Metric(EnergyMetricDescriptor.AverageMetrionCpuEnergyPerIteration, double.NaN);
                yield break;
            }

            var energyUj = energyInterval.EnergyJ * 1_000_000;

            var samples = results.Measurements
                .Where(m => m.IterationMode == IterationMode.Workload &&
                            m.IterationStage == IterationStage.Actual)
                .ToList();

            var energyPerOpSeries = samples
                .Where(m => m.Operations > 0)
                .Select(m => energyUj / m.Operations)
                .ToArray();

            var energyPerOp = energyPerOpSeries.Length > 0 ? energyPerOpSeries.Average() : double.NaN;
            var energyPerIter = samples.Any() ? energyUj / samples.Count : double.NaN;

            yield return new Metric(EnergyMetricDescriptor.AverageMetrionCpuEnergyPerOperation, energyPerOp);
            yield return new Metric(EnergyMetricDescriptor.AverageMetrionCpuEnergyPerIteration, energyPerIter);
        }

        public void DisplayResults(ILogger logger)
        {
            if (!energyIntervals.Any())
            {
                return;
            }

            logger.WriteLineInfo($"Metrion measured the energy for a total of {energyIntervals.Count} benchmarks.");
        }

        public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
        {
            energyIntervals.TryGetValue(parameters.BenchmarkCase, out var energyInterval);

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
                    energyInterval.StartTimestamp = DateTime.Now;
                    break;
                case HostSignal.AfterActualRun:
                    energyInterval.EndTimestamp = DateTime.Now;
                    break;
                case HostSignal.AfterAll:
                    StopMetrion(parameters);
                    AnalyzeMetrionDatabase(parameters);
                    break;
            }

            energyIntervals.Remove(parameters.BenchmarkCase);
            energyIntervals.Add(parameters.BenchmarkCase, energyInterval);
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

        private class EnergyMetricDescriptor : IMetricDescriptor
        {
            internal static IMetricDescriptor AverageMetrionCpuEnergyPerOperation =
                new EnergyMetricDescriptor(
                    $"AvgMetrionCpuEnergyPerOp",
                    Column.MetrionCpuEnergyPerOp,
                    $"Average CPU core energy consumed per benchmark operation (uJ), captured with Metrion.",
                    numberFormat: "#0.00");

            internal static IMetricDescriptor AverageMetrionCpuEnergyPerIteration =
                new EnergyMetricDescriptor(
                    $"AvgMetrionCpuEnergyPerIter",
                    Column.MetrionCpuEnergyPerIter,
                    $"Average CPU core energy consumed per benchmark iteration (uJ), captured with Metrion.",
                    numberFormat: "#0.00");

            private EnergyMetricDescriptor(string id, string columnName, string legend,
                string numberFormat = "#0.00 uj", string unit = "uJ")
            {
                Id = id;
                DisplayName = columnName;
                Legend = legend;
                NumberFormat = numberFormat;
                Unit = unit;
            }

            public string Id { get; }
            public string DisplayName { get; }
            public string Legend { get; }
            public string NumberFormat { get; }
            public UnitType UnitType => UnitType.Dimensionless;
            public string Unit { get; }
            public bool TheGreaterTheBetter => false;
            public int PriorityInCategory { get; }
            public bool GetIsAvailable(Metric metric)
                => !double.IsNaN(metric.Value) && metric.Value != 0.0;
        }
    }


}