namespace SalonCrm.Api.Data.Configurations;

internal static class EnumChecks
{
    // SQL for a check constraint allowing only the names of enum T (enums are stored as text).
    public static string In<T>(string column) where T : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetNames<T>().Select(n => $"'{n}'"))})";
}
