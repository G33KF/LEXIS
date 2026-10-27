using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AleappReader.App.ViewModels;
using AleappReader.Core;

namespace AleappReader.App.Views;

/// <summary>The read-only SQLite reader: one window per file, its temporary copy deleted on closing.</summary>
public partial class SqliteWindow : Window
{
    SqliteViewModel Vm => (SqliteViewModel)DataContext;

    SqliteWindow(SqliteViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SqliteViewModel.Columns)) BuildColumns(); };
        BuildColumns();
        Closed += (_, _) => vm.Dispose();
    }

    /// <summary>Opens the reader on a copy of <paramref name="path"/> (itself a copy of the evidence, or a file the
    /// examiner picked); sidecars are taken from beside it unless given.</summary>
    public static void Show(string path, string name, string origin, string? wal = null, string? shm = null, string? journal = null)
    {
        SqliteFile f;
        try { f = SqliteFile.Open(path, name, wal, shm, journal); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Cannot copy the database: " + e.Message, "SQLite reader", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        new SqliteWindow(new SqliteViewModel(f, origin)) { Owner = Application.Current.MainWindow }.Show();
    }

    void BuildColumns()
    {
        Grid.Columns.Clear();
        for (var i = 0; i < Vm.Columns.Count; i++)
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = Vm.Columns[i], Binding = new Binding($"Cells[{i}]"), SortMemberPath = i.ToString(),
                MaxWidth = 420, CanUserSort = Vm.Paged,
            });
    }

    void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (Vm.Paged) Vm.SortBy(int.Parse(e.Column.SortMemberPath));
    }

    // the selected cell first (a click, the keyboard), else the current one: either can change without the other
    void OnCellChanged(object? sender, EventArgs e)
    {
        var c = Grid.SelectedCells.Count > 0 ? Grid.SelectedCells[0] : Grid.CurrentCell;
        Vm.Inspect(c.Item as SqlRow, c.Column is { } col ? Grid.Columns.IndexOf(col) : -1);
    }
}
