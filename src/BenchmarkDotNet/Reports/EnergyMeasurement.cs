namespace BenchmarkDotNet.Reports;

/// <summary>
/// Represents energy measurements for a specific CPU socket.
/// </summary>
public class EnergyMeasurement
{
    public int SocketId { get; }
    public double PackageEnergy { get; }
    public double DramEnergy { get; }
    public double CoreEnergy { get; }
    public double UncoreEnergy { get; }
    public double PsysEnergy { get; }
    public double AverageCpuTemperature { get; }

    public EnergyMeasurement(int socketId, double packageEnergy, double dramEnergy, double coreEnergy, double uncoreEnergy, double psysEnergy, double averageCpuTemperature)
    {
        SocketId = socketId;
        PackageEnergy = packageEnergy;
        DramEnergy = dramEnergy;
        CoreEnergy = coreEnergy;
        UncoreEnergy = uncoreEnergy;
        PsysEnergy = psysEnergy;
        AverageCpuTemperature = averageCpuTemperature;
    }

    public static EnergyMeasurement Error(int socketId = 0) => new EnergyMeasurement(socketId, 0, 0, 0, 0, 0, 0);
}