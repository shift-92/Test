namespace UniversalTables.Query;

// Инклюзивные границы также позволяют корректно обрабатывать последний день 9999-го года без переполнения.
public sealed record CalendarPeriod(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;
    public static DateOnly? Date(object? value) => value switch
    {
        DateOnly day => day,
        DateTime time => DateOnly.FromDateTime(time),
        _ => null
    };
}