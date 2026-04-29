using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Detectors;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Helpers;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Portability;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using JetBrains.Annotations;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace BenchmarkDotNet.Diagnosers
{
    public class EnergyInterval
    {
        public int ProcessId { get; set; }
        public DateTime StartTimestamp { get; set; }
        public DateTime EndTimestamp { get; set; }
        public double EnergyJ { get; set; }
        public FileInfo TraceFile { get; set; }
        public List<DateTime> IterationTimestamps { get; } = new();
        public List<double> EnergyPerIteration { get; } = new();
    }

    public class MetrionEnergyProfiler : IProfiler
    {
        public static readonly IDiagnoser Default = new MetrionEnergyProfiler(new MetrionEnergyProfilerConfig("/home/test/tools/metrion-internal/.venv/bin/metrion", "/home/test/tools/metrion-internal", keepMetrionDatabaseFiles: true));
        private readonly MetrionEnergyProfilerConfig config;
        private readonly Dictionary<BenchmarkCase, EnergyInterval> energyIntervals = new();
        private EventPipeSession session;
        private Task eventTask;
        private Process? metrionProcess;
        private DirectoryInfo artifactsPath;

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
            logger.WriteLineInfo($"{nameof(MetrionEnergyProfiler)}: Analyzing latest database file.");

            var latestMetrionDbFile = Directory
                .GetFiles(config.MetrionDatabaseDirectory.FullName, config.MetrionDatabaseNamePattern)
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (latestMetrionDbFile == null)
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: The latest database file not found: {config.MetrionDatabaseDirectory.FullName}");
                return;
            }

            string dbPathArg = $"--db-path {latestMetrionDbFile.FullName}";

            if (!energyIntervals.TryGetValue(parameters.BenchmarkCase, out var energyInterval))
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: Missing energy interval for benchmark case '{parameters.BenchmarkCase}' in {nameof(energyIntervals)}.");
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

            /*// Process energy per iteration
            if (energyInterval.IterationTimestamps.Count % 2 == 0)
            {
                energyInterval.IterationTimestamps.Sort();
                logger.WriteLineInfo($"{nameof(MetrionEnergyProfiler)}: Start processing the energy for each iteration.");

                for (int i = 0; i < energyInterval.IterationTimestamps.Count / 2; i++)
                {
                    var startIndex = i * 2;
                    var endIndex = i * 2 + 1;

                    var startTimestamp = energyInterval.IterationTimestamps[startIndex].ToString("yyyy-MM-dd HH:mm:ss");
                    var endTimestamp = energyInterval.IterationTimestamps[endIndex].ToString("yyyy-MM-dd HH:mm:ss");

                    startTimeArg = $"--start-time \"{startTimestamp}\"";
                    endTimeArg = $"--end-time \"{endTimestamp}\"";

                    processStartInfo = new ProcessStartInfo
                    {
                        FileName = config.MetrionBinaryPath.FullName,
                        Arguments = $"analyze --no-plots --export-summary {startTimeArg} {endTimeArg} {dbPathArg} {pidsArg}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        WorkingDirectory = config.MetrionDatabaseDirectory.FullName,
                    };

                    logger.WriteLineInfo($"// Execute: {processStartInfo.FileName} {processStartInfo.Arguments} in {processStartInfo.WorkingDirectory}");
                    analyzeProcess = Process.Start(processStartInfo);

                    if (analyzeProcess == null)
                        return;

                    if (!analyzeProcess.WaitForExit(1000 * 10))
                    {
                        analyzeProcess.KillTree();
                    }

                    analyzeProcess.Dispose();

                    double iterationEnergy = ExtractLatestMetrionEnergyMeasurement(logger, energyInterval.ProcessId);
                    energyInterval.EnergyPerIteration.Add(iterationEnergy);
                }
            }
            else
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: The number of iteration timestamps is odd ({energyInterval.IterationTimestamps.Count}), unable to calculate energy per iteration.");
            }*/

            if (config.KeepMetrionDatabaseFiles)
            {
                var traceFilePath = new FileInfo(ArtifactFileNameHelper.GetTraceFilePath(parameters, latestMetrionDbFile.CreationTime, $"pid{energyInterval.ProcessId}.db"));
                File.Move(latestMetrionDbFile.FullName, traceFilePath.FullName);
                energyIntervals[parameters.BenchmarkCase].TraceFile = traceFilePath;
            }
        }

        private double ExtractLatestMetrionEnergyMeasurement(ILogger logger, int processId)
        {
            DirectoryInfo measurementsDirectory = new DirectoryInfo(Path.Combine(config.MetrionDatabaseDirectory.FullName, "metrion/energy_attribution/output/"));

            if (!measurementsDirectory.Exists)
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: Unable to find measurements directory: {measurementsDirectory.FullName}");
                return 0;
            }

            var latestMetrionOutputFile = Directory
                .GetFiles(measurementsDirectory.FullName, "*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (latestMetrionOutputFile == null)
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: The measurements were not found in the directory {measurementsDirectory.FullName}");
                return 0;
            }

            var jsonFile = File.ReadAllText(latestMetrionOutputFile.FullName);
            if (!jsonFile.Contains($"[{processId}]"))
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: The latest measurement file ({latestMetrionOutputFile.Name}) contains a different processId. Expected: {processId}.");
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

        private string ExportMetrionMeasurementsAsCsv()
        {
            string realSeparator = CsvSeparator.Comma.ToRealSeparator();

            string uniqueString = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var distinctNames = energyIntervals.Select(kvp => kvp.Key.Descriptor.Type.Name).Distinct();
            var pathPrefix = distinctNames.Count() == 1 ? distinctNames.First() : "All";
            var filePath = Path.Combine(artifactsPath.FullName, "results", $"{pathPrefix}_metrion_measurements_{uniqueString}.csv");

            using (var stream = new StreamWriter(filePath, append: false))
            {
                using (var streamLogger = new StreamLogger(stream))
                {
                    // Writing header
                    streamLogger.Write("Target");
                    streamLogger.Write(realSeparator);

                    streamLogger.Write("TargetMethod");
                    streamLogger.Write(realSeparator);

                    streamLogger.Write("Measurement_IterationIndex");
                    streamLogger.Write(realSeparator);

                    streamLogger.Write("Measurement_MetrionEnergy");
                    streamLogger.WriteLine();

                    // Writing lines
                    foreach (var kvp in energyIntervals)
                    {
                        var benchmarkCase = kvp.Key;
                        var energyInterval = kvp.Value;

                        for (int i = 0; i < energyInterval.EnergyPerIteration.Count; ++i)
                        {
                            streamLogger.Write(CsvHelper.Escape(benchmarkCase.Descriptor.Type.Name, realSeparator));
                            streamLogger.Write(realSeparator);

                            streamLogger.Write(CsvHelper.Escape(benchmarkCase.Descriptor.WorkloadMethodDisplayInfo, realSeparator));
                            streamLogger.Write(realSeparator);

                            streamLogger.Write(i.ToString());
                            streamLogger.Write(realSeparator);

                            streamLogger.Write(CsvHelper.Escape(energyInterval.EnergyPerIteration[i].ToString(), realSeparator));
                            streamLogger.WriteLine();
                        }

                    }
                }
            }

            return filePath;
        }

        public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
        {
            if (!energyIntervals.TryGetValue(results.BenchmarkCase, out var energyInterval))
            {
                yield return new Metric(EnergyMetricDescriptor.AverageMetrionCpuEnergyPerOperation, double.NaN);
                yield return new Metric(EnergyMetricDescriptor.AverageMetrionCpuEnergyPerIteration, double.NaN);
                yield break;
            }

            //var energyUj = energyInterval.EnergyJ * 1_000_000;

            var samples = results.Measurements
                .Where(m => m.IterationMode == IterationMode.Workload &&
                            m.IterationStage == IterationStage.Actual)
                .ToList();

            var perOpSeries = samples
                .Where(m => (m.Operations > 0))
                .Select(m => energyInterval.EnergyPerIteration.Count > m.IterationIndex ? energyInterval.EnergyPerIteration[m.IterationIndex] / m.Operations : double.NaN)
                .ToArray();

            var energyPerOp = perOpSeries.Length > 0 ? perOpSeries.Average() : double.NaN;
            var energyPerIter = energyInterval.EnergyPerIteration.Any() ? energyInterval.EnergyPerIteration.Average() : double.NaN;

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

            if (config.KeepMetrionDatabaseFiles)
            {
                logger.WriteLineInfo($"The database files for each benchmark were stored in the Artifacts folder. e.g: ");
                logger.WriteLineInfo($"{energyIntervals.First().Value.TraceFile.FullName}");
            }

            var exportedFilePath = ExportMetrionMeasurementsAsCsv();
            logger.WriteLineInfo("Metrion measurements exported to " + exportedFilePath);
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
                    StartEventProfiling(parameters);
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
                    StopEventProfiling();
                    AnalyzeMetrionDatabase(parameters);
                    break;
            }

            energyIntervals.Remove(parameters.BenchmarkCase);
            energyIntervals.Add(parameters.BenchmarkCase, energyInterval);
        }

        private void StartEventProfiling(DiagnoserActionParameters parameters)
        {
            var pid = parameters.Process.Id;
            var client = new DiagnosticsClient(pid);

            var providers = new[]
            {
                new EventPipeProvider(
                    EngineEventSource.SourceName,
                    EventLevel.Informational,
                    long.MaxValue)
            };

            this.session = client.StartEventPipeSession(providers, false);

            this.eventTask = Task.Run(() =>
            {
                using (var source = new EventPipeEventSource(this.session.EventStream))
                {
                    source.Dynamic.All += (TraceEvent data) => AddIterationTimestamp(parameters.BenchmarkCase, data);
                    source.Process();
                }
            });
        }

        private void StopEventProfiling()
        {
            this.session?.Stop();
            this.session?.Dispose();

            // Wait for the event processing task to complete gracefully
            this.eventTask?.Wait();
        }

        private void AddIterationTimestamp(BenchmarkCase benchmarkCase, TraceEvent traceEvent)
        {
            switch ((int)traceEvent.ID)
            {
                case EngineEventSource.WorkloadActualStartEventId:
                case EngineEventSource.WorkloadActualStopEventId:
                    if (energyIntervals.TryGetValue(benchmarkCase, out var energyInterval))
                        energyInterval.IterationTimestamps.Add(traceEvent.TimeStamp);
                    break;
            }
        }



        public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
        {
            if (!OsDetector.IsLinux())
            {
                yield return new ValidationError(true, "MetrionEnergyProfiler works only on Linux!");
            }
            if (!config.MetrionBinaryPath.Exists)
            {
                yield return new ValidationError(true, "Metrion Binary not found!");
            }

            artifactsPath = new DirectoryInfo(validationParameters.Config.ArtifactsPath);
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
                        logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: kill(metrion, SIGINT) failed with {lastError}");
                    }

                    if (!metrionProcess.WaitForExit((int)config.Timeout.TotalMilliseconds))
                    {
                        logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: The Metrion script did not stop in {config.Timeout.TotalSeconds}s. It's going to be force killed now.");
                        metrionProcess.KillTree(); // kill the entire process tree
                    }
                }
                else
                {
                    logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: For some reason the metrion script has finished sooner than expected.");
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