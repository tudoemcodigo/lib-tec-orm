using System.ComponentModel;
using System.Diagnostics;

namespace TEC.ORM.EntityFrameworkCore.Security;

/// <summary>
/// Representa uma string de conexão protegida em trânsito.
/// Impede estritamente a exibição das credenciais em logs, interpolações acidentais,
/// depuradores (Debugger Viewer / Locals / Autos) e IntelliSense.
/// </summary>
[DebuggerDisplay("***")]
public sealed class ProtectedConnectionString
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    private readonly string _value;

    /// <summary>Cria uma nova instância protegida.</summary>
    public ProtectedConnectionString(string value)
    {
        _value = value ?? string.Empty;
    }

    /// <summary>
    /// Revela a string de conexão pura. Uso estritamente restrito à atribuição no driver ADO.NET.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string Reveal() => _value;

    /// <summary>
    /// Alias para <see cref="Reveal"/>, recupera o valor subjacente da string de conexão protegida.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string Unprotect() => _value;

    /// <inheritdoc />
    public override string ToString() => "***";
}
