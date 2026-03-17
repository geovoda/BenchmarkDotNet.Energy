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
                            m.IterationStage == IterationStage.Result)
                .ToList();

            // Per-iteration uJ/op samples
            var pkgPerOpSeries = samples.Where(m => m.Operations > 0).Select(m => m.PackageEnergy / m.Operations).ToArray();
            var dramPerOpSeries = samples.Where(m => m.Operations > 0).Select(m => m.DramEnergy / m.Operations).ToArray();

            // Mean across iterations
            double pkgPerOp  = pkgPerOpSeries.Length > 0 ? pkgPerOpSeries.Average()  : double.NaN;
            double dramPerOp = dramPerOpSeries.Length > 0 ? dramPerOpSeries.Average() : double.NaN;

            // Variability (sample SD + CV%)
            double pkgPerOpStdDev = StdDevSample(pkgPerOpSeries, pkgPerOp);
            double pkgPerOpCvPct = (pkgPerOp > 0 && !double.IsNaN(pkgPerOpStdDev))
                ? (pkgPerOpStdDev / pkgPerOp) * 100.0
                : double.NaN;

            // uJ/iteration (mean iteration energy)
            double dramPerIter = samples.Any() ? samples.Average(m => m.DramEnergy) : double.NaN;
            double pkgPerIter  = samples.Any() ? samples.Average(m => m.PackageEnergy) : double.NaN;

            yield return new Metric(EnergyMetricDescriptor.DramEnergyPerOp, dramPerOp);
            yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerOp, pkgPerOp);
            yield return new Metric(EnergyMetricDescriptor.DramEnergyPerIteration, dramPerIter);
            yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerIteration, pkgPerIter);

            yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerOpStdDev, pkgPerOpStdDev);
            yield return new Metric(EnergyMetricDescriptor.PackageEnergyPerOpCvPct, pkgPerOpCvPct);
        }
        private static double StdDevSample(double[] values, double mean)
        {
            int n = values.Length;
            if (n < 2) return double.NaN;

            double sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                double d = values[i] - mean;
                sumSq += d * d;
            }
            return Math.Sqrt(sumSq / (n - 1));
        }

        private class EnergyMetricDescriptor : IMetricDescriptor
        {
            internal static readonly IMetricDescriptor DramEnergyPerOp =
                new EnergyMetricDescriptor(
                    "AvgEnergyDramPerOp",
                    Column.DramEnergyPerOp,
                    "Average DRAM energy consumed per operation (uJ).");

            internal static readonly IMetricDescriptor PackageEnergyPerOp =
                new EnergyMetricDescriptor(
                    "AvgEnergyPackagePerOp",
                    Column.PackageEnergyPerOp,
                    "Average CPU package energy consumed per operation (uJ).");

            internal static readonly IMetricDescriptor PackageEnergyPerOpStdDev =
                new EnergyMetricDescriptor(
                    "PkgEPerOpStdDev",
                    Column.PackageEnergyPerOpStdDev,
                    "Standard deviation of package energy per operation (uJ/op).",
                    unit: "uJ");

            internal static readonly IMetricDescriptor PackageEnergyPerOpCvPct =
                new EnergyMetricDescriptor(
                    "PkgEPerOpCvPct",
                    Column.PackageEnergyPerOpCvPct,
                    "Coefficient of variation of package energy per operation (%).",
                    numberFormat: "#0.00", // no "uj"
                    unit: "");

            internal static readonly IMetricDescriptor DramEnergyPerIteration =
                new EnergyMetricDescriptor(
                    "AvgEnergyDramPerIter",
                    Column.DramEnergyPerIter,
                    "Average DRAM energy consumed per benchmark iteration (uJ).");

            internal static readonly IMetricDescriptor PackageEnergyPerIteration =
                new EnergyMetricDescriptor(
                    "AvgEnergyPackagePerIter",
                    Column.PackageEnergyIter,
                    "Average CPU package energy consumed per benchmark iteration (uJ).");

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