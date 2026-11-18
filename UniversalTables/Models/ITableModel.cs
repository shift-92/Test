namespace UniversalTables.Models;

public interface ITableModel
{
    Guid Id { get; set; }
    string ToDescription();
}