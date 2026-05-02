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

            if (!energyIntervals.TryGetValue(parameters.BenchmarkCase, out var energyInterval))
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: Missing energy interval for benchmark case '{parameters.BenchmarkCase}' in {nameof(energyIntervals)}.");
                return;
            }

            string dbPathArg = $"--db-path {latestMetrionDbFile.FullName}";
            string pidsArg = $"--filter-pids {energyInterval.ProcessId}";
            string startTimeArg = $"--start-time \"{energyInterval.StartTimestamp.ToString("yyyy-MM-dd HH:mm:ss")}\"";
            string endTimeArg = $"--end-time \"{energyInterval.EndTimestamp.ToString("yyyy-MM-dd HH:mm:ss")}\"";

            RunMetrionAnalyzeProcess(logger, $"analyze --no-plots --export-summary --export-raw-data {startTimeArg} {endTimeArg} {dbPathArg} {pidsArg}");
            energyIntervals[parameters.BenchmarkCase].EnergyJ = ExtractLatestMetrionEnergyMeasurement(logger, energyInterval.ProcessId);

            if (config.MeasurePerIteration)
                AnalyzeMetrionDatabasePerIteration(logger, energyInterval);

            if (config.KeepMetrionDatabaseFiles)
            {
                var traceFilePath = new FileInfo(ArtifactFileNameHelper.GetTraceFilePath(parameters, latestMetrionDbFile.CreationTime, $"pid{energyInterval.ProcessId}.db"));
                File.Move(latestMetrionDbFile.FullName, traceFilePath.FullName);
                energyIntervals[parameters.BenchmarkCase].TraceFile = traceFilePath;
            }
            else
            {
                latestMetrionDbFile.Delete();
            }
        }

        private void RunMetrionAnalyzeProcess(ILogger logger, string arguments)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = config.MetrionBinaryPath.FullName,
                Arguments = arguments,
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
                analyzeProcess.KillTree();

            analyzeProcess.Dispose();
        }

        private void AnalyzeMetrionDatabasePerIteration(ILogger logger, EnergyInterval energyInterval)
        {
            if (energyInterval.IterationTimestamps.Count % 2 != 0)
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: The number of iteration timestamps is odd ({energyInterval.IterationTimestamps.Count}), unable to calculate energy per iteration.");
                return;
            }

            var rawMeasurements = ReadRawMetrionCpuMeasurements(logger);
            if (rawMeasurements.Length == 0)
                return;

            energyInterval.IterationTimestamps.Sort();
            logger.WriteLineInfo($"{nameof(MetrionEnergyProfiler)}: Start processing the energy for each iteration.");

            for (int i = 0; i < energyInterval.IterationTimestamps.Count / 2; i++)
            {
                var iterStart = energyInterval.IterationTimestamps[i * 2];
                var iterEnd = energyInterval.IterationTimestamps[i * 2 + 1];

                double iterationEnergy = rawMeasurements
                    .Sum(m => CalculateOverlappingEnergy(m.Item1, m.Item2, m.Item3, iterStart, iterEnd));

                energyInterval.EnergyPerIteration.Add(iterationEnergy);
            }
        }

        private static double CalculateOverlappingEnergy(DateTime mStart, DateTime mEnd, double energy, DateTime iterStart, DateTime iterEnd)
        {
            var overlapStart = mStart > iterStart ? mStart : iterStart;
            var overlapEnd = mEnd < iterEnd ? mEnd : iterEnd;

            if (overlapEnd <= overlapStart)
                return 0;

            var overlapFraction = (overlapEnd - overlapStart).TotalSeconds / (mEnd - mStart).TotalSeconds;
            return energy * overlapFraction;
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

        private (DateTime, DateTime, double)[] ReadRawMetrionCpuMeasurements(ILogger logger)
        {
            DirectoryInfo measurementsDirectory = new DirectoryInfo(Path.Combine(config.MetrionDatabaseDirectory.FullName, "metrion/energy_attribution/output/"));

            if (!measurementsDirectory.Exists)
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: Unable to find measurements directory: {measurementsDirectory.FullName}");
                return [];
            }

            var latestMetrionOutputFile = Directory
                .GetFiles(measurementsDirectory.FullName, "raw_cpu*.csv")
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (latestMetrionOutputFile == null)
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: The measurements were not found in the directory {measurementsDirectory.FullName}");
                return [];
            }

            // Read the content of latestMetrionOutputFile
            var csvFile = File.ReadAllLines(latestMetrionOutputFile.FullName);

            if (csvFile.Length < 2)
            {
                logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: The measurements file {latestMetrionOutputFile.FullName} does not contain enough data.");
                return [];
            }

            var headers = csvFile[0].Split(',');

            int INTERVAL_START_INDEX = Array.FindIndex(headers, h => h == "interval_start");
            int INTERVAL_END_INDEX = Array.FindIndex(headers, h => h == "interval_end");
            int TOTAL_ENERGY_INDEX = Array.FindIndex(headers, h => h == "total_energy_j");

            int MAX_INDEX = Math.Max(Math.Max(INTERVAL_START_INDEX, INTERVAL_END_INDEX), TOTAL_ENERGY_INDEX);

            (DateTime, DateTime, double)[] measurements = new (DateTime, DateTime, double)[csvFile.Length - 1];

            for (int i = 1; i < csvFile.Length; i++) // Skip header
            {
                var line = csvFile[i];
                var parts = line.Split(',');

                if (parts.Length <= MAX_INDEX)
                {
                    logger.WriteLineError($"{nameof(MetrionEnergyProfiler)}: The line '{line}' does not contain enough columns.");
                    continue;
                }

                var startDateTime = DateTime.ParseExact(parts[INTERVAL_START_INDEX], "yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                var endDateTime = DateTime.ParseExact(parts[INTERVAL_END_INDEX], "yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                var totalEnergy = double.TryParse(parts[TOTAL_ENERGY_INDEX], out double energy) ? energy : double.NaN;

                measurements[i - 1] = (startDateTime, endDateTime, totalEnergy);
            }

            return measurements;
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

            var samples = results.Measurements
                .Where(m => m.IterationMode == IterationMode.Workload &&
                            m.IterationStage == IterationStage.Actual)
                .ToList();

            double energyPerOp, energyPerIter;

            if (config.MeasurePerIteration && energyInterval.EnergyPerIteration.Any())
            {
                var perOpSeries = samples
                    .Where(m => m.Operations > 0)
                    .Select(m => energyInterval.EnergyPerIteration.Count > m.IterationIndex
                        ? energyInterval.EnergyPerIteration[m.IterationIndex] / m.Operations
                        : double.NaN)
                    .ToArray();

                energyPerOp = perOpSeries.Length > 0 ? perOpSeries.Average() : double.NaN;
                energyPerIter = energyInterval.EnergyPerIteration.Average();
            }
            else
            {
                var energyUj = energyInterval.EnergyJ * 1_000_000;
                var totalOps = samples.Where(m => m.Operations > 0).Sum(m => m.Operations);
                energyPerOp = totalOps > 0 ? energyUj / totalOps : double.NaN;
                energyPerIter = samples.Any() ? energyUj / samples.Count : double.NaN;
            }

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

            if (config.MeasurePerIteration)
            {
                var exportedFilePath = ExportMetrionMeasurementsAsCsv();
                logger.WriteLineInfo("Metrion measurements exported to " + exportedFilePath);
            }
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
                    if (config.MeasurePerIteration)
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
                    if (config.MeasurePerIteration)
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
                RedirectStandardOutput = false,
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