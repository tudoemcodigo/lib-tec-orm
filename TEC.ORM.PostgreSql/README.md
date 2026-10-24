<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# TEC.ORM.PostgreSql

Provedor oficial do **TEC.ORM** para **PostgreSQL** com Entity Framework Core (`Npgsql.EntityFrameworkCore.PostgreSQL`) e Dapper (`Npgsql`).

## Recursos

- **Configuração Simples via Container**: Extensão `AddTecOrmPostgreSql<TContext>()`.
- **Credenciais do Cofre**: Strings de conexão resolvidas via `TEC.Vault` com `SslMode` obrigatório (`Require`, `VerifyCA` ou `VerifyFull`).
- **Reserva Atômica de Idempotência**: Cláusula nativa `INSERT ... ON CONFLICT ("Scope", "IdempotencyKey") DO NOTHING`.
- **Validador Somente Leitura**: Bloqueio de DML, `FOR UPDATE`, `FOR SHARE`, comentários aninhados, delimitadores de dólar (`$$`) e funções de sistema (`pg_*`, `dblink`, `lo_*`).
- **Tradução de Erros Npgsql**: Mapeamento preciso de SqlState (23505/23503 para conflito, 40001/40P01 para concorrência/deadlock e falhas transitórias).

## Instalação

```bash
dotnet add package TEC.ORM.PostgreSql --version 0.0.1
```

## Uso

```csharp
builder.Services.AddTecOrmPostgreSql<AppDbContext>(options =>
{
    options.ConnectionSecretName = "postgres-app-rw";
    options.ReadOnlyConnectionSecretName = "postgres-app-ro";
});
```
