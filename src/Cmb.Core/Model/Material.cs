namespace Cmb.Core.Model;

public sealed record Material(int Id, string Name, double E, double Nu, double Rho)
{
    public static Material DefaultSteel => new(1, "Steel", 206000.0, 0.3, 7.85e-9);
}
