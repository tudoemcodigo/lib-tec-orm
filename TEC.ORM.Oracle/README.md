<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# TEC.ORM.Oracle

Provedor oficial do **TEC.ORM** para **Oracle Database** com Entity Framework Core (`Oracle.EntityFrameworkCore`) e Dapper (`Oracle.ManagedDataAccess`).

## Recursos

- **Configuração Simples via Container**: Extensão `AddTecOrmOracle<TContext>()`.
- **Credenciais do Cofre**: Strings de conexão resolvidas via `TEC.Vault` com exigência de conexões criptografadas TCPS.
- **Reserva Atômica de Idempotência**: Instrução nativa `MERGE INTO ... USING (SELECT ... FROM DUAL)`.
- **Validador Somente Leitura**: Bloqueio de DML, `FOR UPDATE`, sintaxe de literais `q'[...]'` e chamadas a pacotes do sistema (`DBMS_*`, `UTL_*`).
- **Tradução de Erros ODP.NET**: Mapeamento de códigos de erro ORA para conflito, concorrência, timeouts e falhas de conexão.

## Instalação

```bash
dotnet add package TEC.ORM.Oracle --version 0.0.1
```

## Uso

```csharp
builder.Services.AddTecOrmOracle<AppDbContext>(options =>
{
    options.ConnectionSecretName = "oracle-app-rw";
    options.ReadOnlyConnectionSecretName = "oracle-app-ro";
});
```
