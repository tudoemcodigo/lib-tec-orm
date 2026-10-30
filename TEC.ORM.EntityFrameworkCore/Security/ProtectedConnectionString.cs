using System.ComponentModel;
using System.Diagnostics;

namespace TEC.ORM.EntityFrameworkCore.Security;

/// <summary>
/// String de conexão em trânsito. <see cref="ToString"/>, o depurador e o IntelliSense ocultam o valor, para que um log ou uma
/// interpolação acidental nunca exponha credenciais.
/// </summary>
/// <remarks>Não há criptografia em memória: o texto puro só sai por <see cref="Reveal"/>, usado na atribuição ao driver.</remarks>
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
    /// A política aceitou uma conexão sem validação completa do servidor (ex.: <c>TrustServerCertificate=True</c>,
    /// <c>SslMode=Require</c>, <c>PROTOCOL=TCP</c>), liberada por <c>AllowTrustServerCertificate</c>: gera o aviso 3103.
    /// </summary>
    internal bool RelaxedTransportSecurity { get; init; }

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
