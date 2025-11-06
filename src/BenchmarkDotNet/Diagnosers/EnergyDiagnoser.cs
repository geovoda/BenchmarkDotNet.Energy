using System;
using System.Collections.Generic;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
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

        public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters) => Array.Empty<ValidationError>();

        public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
        {
            System.Console.WriteLine(DiagnoserId + " Signal " + signal.ToString());
        }

        public IEnumerable<Metric> ProcessResults(DiagnoserResults diagnoserResults)
        {
            // TODO AAU: Check if this avg should be processed before
            double dramEnergyAvg = 0.0;
            double packageEnergyAvg = 0.0;

            foreach (var measurement in diagnoserResults.Measurements)
            {
                // TODO AAU: Check if should check the iteration mode and iteration stage
                dramEnergyAvg += measurement.DramEnergy / measurement.Operations;
                packageEnergyAvg += measurement.PackageEnergy / measurement.Operations;
            }

            if (diagnoserResults.Measurements.Count > 0)
            {
                dramEnergyAvg /=  diagnoserResults.Measurements.Count;
                packageEnergyAvg /=  diagnoserResults.Measurements.Count;
            }

            yield return new Metric(EnergyMetricDescriptor.DramEnergy, dramEnergyAvg);
            yield return new Metric(EnergyMetricDescriptor.PackageEnergy, packageEnergyAvg);
        }

        private class EnergyMetricDescriptor : IMetricDescriptor
        {
            internal static readonly IMetricDescriptor DramEnergy = new EnergyMetricDescriptor("Dram", Column.DramEnergy);
            internal static readonly IMetricDescriptor PackageEnergy = new EnergyMetricDescriptor("Package", Column.PackageEnergy);
            private EnergyMetricDescriptor(string id, string columnName)
            {
                Id = $"AvgEnergy{id}";
                DisplayName = columnName;
                Legend = $"Average energy of the CPU {id} in Joules";
            }

            public string Id { get; }

            public string DisplayName { get; }
            public string Legend { get; }
            public string NumberFormat => "#0.00";
            public UnitType UnitType => UnitType.Dimensionless;
            public string Unit => "J";
            public bool TheGreaterTheBetter => false;
            public int PriorityInCategory { get; }
            public bool GetIsAvailable(Metric metric) => true;
        }
    }
}