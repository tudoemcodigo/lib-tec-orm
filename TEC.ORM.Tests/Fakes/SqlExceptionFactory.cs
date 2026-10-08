using System.Reflection;
using Microsoft.Data.SqlClient;

namespace TEC.ORM.Tests.Fakes;

/// <summary>
/// Cria <see cref="SqlException"/> com o número de erro desejado (o SqlClient não tem construtor público): para testar a
/// tradução e as novas tentativas sem banco.
/// </summary>
internal static class SqlExceptionFactory
{
    public static SqlException Create(int number, byte errorClass = 16)
    {
        var errorConstructor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        object?[] arguments = errorConstructor.GetParameters().Select(object? (parameter) => parameter.Name switch
        {
            "infoNumber" => number,
            "errorClass" => errorClass,
            "errorState" => (byte)1,
            _ when parameter.ParameterType == typeof(string) => "teste",
            _ when parameter.ParameterType == typeof(int) => 0,
            _ when parameter.ParameterType == typeof(uint) => 0u,
            _ when parameter.ParameterType == typeof(byte) => (byte)0,
            _ => null
        }).ToArray();
        var error = (SqlError)errorConstructor.Invoke(arguments);

        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(errors, [error]);

        var create = typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(SqlErrorCollection), typeof(string)])!;
        return (SqlException)create.Invoke(null, [errors, "16.00.0000"])!;
    }
}
