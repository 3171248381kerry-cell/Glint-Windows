using System.Runtime.InteropServices;

namespace Glint.Windows.Services;

/// <summary>Windows ships this SQLite runtime; no writable database or external native dependency.</summary>
internal sealed class ReadOnlySqlite : IDisposable
{
    private nint database;

    public ReadOnlySqlite(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Bundled dictionary is missing.");
        if (sqlite3_open_v2(path, out database, 1 /* SQLITE_OPEN_READONLY */, 0) != 0)
        {
            Dispose();
            throw new InvalidDataException("Bundled dictionary could not be opened.");
        }
    }

    public List<string[]> Query(string sql, string argument)
    {
        if (database == 0) return [];
        if (sqlite3_prepare_v2(database, sql, -1, out var statement, 0) != 0)
            throw new InvalidDataException("Bundled dictionary schema is unsupported.");
        try
        {
            if (sqlite3_bind_text(statement, 1, argument, -1, new nint(-1)) != 0)
                throw new InvalidDataException("Dictionary parameter could not be bound.");
            var rows = new List<string[]>();
            int result;
            while ((result = sqlite3_step(statement)) == 100)
            {
                var row = new string[sqlite3_column_count(statement)];
                for (var i = 0; i < row.Length; i++)
                    row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i)) ?? "";
                rows.Add(row);
            }
            if (result != 101) throw new InvalidDataException("Dictionary query could not complete.");
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }

    public void Dispose()
    {
        if (database != 0) { sqlite3_close(database); database = 0; }
    }

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(nint db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint stmt, nint tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(nint stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, nint destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(nint stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(nint stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint sqlite3_column_text(nint stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(nint stmt);
}
