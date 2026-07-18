namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// The four per-module permission actions, mirroring the frontend PermissionGuard
    /// ('view' | 'create' | 'edit' | 'delete') and the RolePermissions columns
    /// (row-exists → view, CanCreate, CanEdit, CanDelete).
    /// </summary>
    public enum PermissionAction
    {
        View,
        Create,
        Edit,
        Delete
    }
}
