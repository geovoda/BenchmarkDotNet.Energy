using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Detectors;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using Perfolizer.Metrology;

namespace BenchmarkDotNet.Diagnosers
{
    public class EnergyDiagnoser: IDiagnoser
    {
        private const string DiagnoserId = nameof(EnergyDiagnoser);

        public static readonly EnergyDiagnoser Default = new EnergyDiagnoser(new EnergyDiagnoserConfig(displayEnergyColumn: true));

        public EnergyDiagnoser(EnergyDiagnoserConfig config) => Config = config;

        public EnergyDiagnoserConfig Config { get; }

        public RunMode GetRunMode(BenchmarkCase benchmarkCase) => RunMode.NoOverhead;

        public IEnumerable<string> Ids => new[] { DiagnoserId };
        public IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();
        public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();
        public void DisplayResults(ILogger logger) { }

        public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
        {
            if (!OsDetector.IsLinux())
            {
                yield return new ValidationError(true, "The EnergyDiagnoser works only on Linux!");
            }
        }

        public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
        {
            System.Console.WriteLine(DiagnoserId + " Signal " + signal.ToString());
        }

        public IEnumerable<Metric> ProcessResults(DiagnoserResults diagnoserResults)
        {
            var samples = diagnoserResults.Measurements
                .Where(m => m.IterationMode == IterationMode.Workload &&
                            m.IterationStage == IterationStage.Actual)
                .ToList();

            long totalOps = samples.Sum(m => m.Operations);

            double totalDram = samples.Sum(m => m.DramEnergy);
            double totalPkg  = samples.Sum(m => m.PackageEnergy);

            // uJ/op
            double dramPerOp = totalOps > 0 ? totalDram / totalOps : double.NaN;
            double pkgPerOp  = totalOps > 0 ? totalPkg  / totalOps : double.NaN;

            // uJ/iteration (average iteration)
            double dramPerIter = samples.Any()
                ? samples.Average(m => m.DramEnergy)
                : double.NaN;

            double pkgPerIter = samples.Any()
                ? samples.Average(m => m.PackageEnergy)
                : double.NaN;

            yield return new Metric(EnergyMetricDescriptor.DramEnergyPerOp, dramPerOp);
            yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerOp, pkgPerOp);
            yield return new Metric(EnergyMetricDescriptor.DramEnergyPerIteration, dramPerIter);
            yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerIteration, pkgPerIter);
        }

        private class EnergyMetricDescriptor : IMetricDescriptor
        {
            internal static readonly IMetricDescriptor DramEnergyPerOp =
                new EnergyMetricDescriptor(
                    "AvgEnergyDramPerOp",
                    Column.DramEnergyPerOp,
                    "Average DRAM energy consumed per operation (µJ).");

            internal static readonly IMetricDescriptor PackageEnergyPerOp =
                new EnergyMetricDescriptor(
                    "AvgEnergyPackagePerOp",
                    Column.PackageEnergyPerOp,
                    "Average CPU package energy consumed per operation (µJ).");

            internal static readonly IMetricDescriptor DramEnergyPerIteration =
                new EnergyMetricDescriptor(
                    "AvgEnergyDramPerIter",
                    Column.DramEnergyPerIter,
                    "Average DRAM energy consumed per benchmark iteration (µJ).");

            internal static readonly IMetricDescriptor PackageEnergyPerIteration =
                new EnergyMetricDescriptor(
                    "AvgEnergyPackagePerIter",
                    Column.PackageEnergyIter,
                    "Average CPU package energy consumed per benchmark iteration (µJ).");

            private EnergyMetricDescriptor(string id, string columnName, string legend)
            {
                Id = id;
                DisplayName = columnName;
                Legend = legend;
            }

            public string Id { get; }
            public string DisplayName { get; }
            public string Legend { get; }
            public string NumberFormat => "#0.00 uj";
            public UnitType UnitType => UnitType.Dimensionless;
            public string Unit => "uJ";
            public bool TheGreaterTheBetter => false;
            public int PriorityInCategory { get; }
            public bool GetIsAvailable(Metric metric)
                => !double.IsNaN(metric.Value) && metric.Value != 0.0;
        }
    }
}