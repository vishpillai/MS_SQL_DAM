namespace SqlDam.Web.Models;

public static class DamRoles
{
    public const string Admin = "Admin";
    public const string Auditor = "Auditor";
    public const string ReadUser = "ReadUser";
    public const string SuperAdmin = "SuperAdmin";
}

public static class DamPolicies
{
    public const string Read = "DamRead";
    public const string Admin = "DamAdmin";
    public const string SuperAdmin = "DamSuperAdmin";
}
