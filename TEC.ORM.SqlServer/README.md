<div align="center">

<img src="https://raw.githubusercontent.com/tudoemcodigo/lib-tec-orm/main/Images/Logo.png" alt="TEC.ORM" width="100" />

# 🗃️ TEC.ORM.SqlServer

**Implementação do TEC.ORM para SQL Server: CRUD com EF Core, leituras com Dapper numa conexão somente leitura, conexão sempre lida de um cofre (TEC.Vault), exclusão lógica, auditoria, transação para o TEC.Cqrs e telemetria sem dados sensíveis.**

[📚 Documentação](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/README.md) · [🗄️ SQL Server](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/sqlserver.md) · [🐙 Repositório](https://github.com/tudoemcodigo/lib-tec-orm)

</div>

## ✨ O que é

- **`AddTecOrm<TContext>`**: registra o contexto e `IOrmRepository<,>`, `IOrmQueryExecutor`, `IUnitOfWork` (TEC.Cqrs),
  `IOrmOperationRunner` e `IOrmConnectionSecurity`; opções validadas na subida.
- **Conexão do cofre**: o `DbContext` não tem string de conexão; ela vem do TEC.Vault na abertura e passa por uma política
  (criptografia obrigatória, `TrustServerCertificate` só com opt-in, `Persist Security Info=False`).
- **Exclusão lógica automática** (filtro global no EF Core 8 e 10, com propagação) e **auditoria** com o `ICurrentUser`.
- **Resiliência**: novas tentativas só em leituras fora de transação; limites `MaxFindResults`, `MaxPageSize` e
  `MaxQueryRows`; pool esgotado e erros transitórios do Azure SQL viram `ORM_CONEXAO_INDISPONIVEL`.
- **Observabilidade**: `ActivitySource`/`Meter` `TEC.ORM` e logs de auditoria, sem SQL, valores ou dados da conexão;
  health check `AddHealthChecks().AddTecOrm()`.
- Sem Native AOT (EF Core e Dapper usam reflexão); exige ICU.

## 🎯 Quando usar

No projeto de infraestrutura ou no host (API, worker) que acessa SQL Server ou Azure SQL. O domínio e a aplicação
referenciam só o [`TEC.ORM`](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/TEC.ORM/README.md), que este pacote traz.

## 📥 Instalação

```bash
dotnet nuget add source https://nuget.pkg.github.com/tudoemcodigo/index.json -n tec-interno -u <usuario> -p <PAT>
dotnet add package TEC.ORM.SqlServer --version 0.0.1
```

> [!IMPORTANT]
> **Use o `TEC.ORM` na mesma versão deste pacote.** Este satélite usa tipos internos do núcleo (`InternalsVisibleTo`) e
> é publicado junto com ele; versões diferentes podem falhar em execução (`MissingMethodException`, `TypeLoadException`).
> O pacote já traz o `TEC.ORM` na versão certa. Depende também do `TEC.Vault` e do `TEC.Cqrs`.

## 🚀 Início rápido

```csharp
using TEC.ORM.SqlServer.DependencyInjection;
using TEC.ORM.SqlServer.HealthChecks;
using TEC.Vault.AzureKeyVault;
using TEC.Vault.DependencyInjection;

builder.Services.AddTecVault(vault => vault
    .UseAzureKeyVault(o => o.VaultUri = new Uri("https://<nome-do-cofre>.vault.azure.net/"))
    .EnableSecretCache(TimeSpan.FromMinutes(5)));

builder.Services.AddTecOrm<SalesContext>(orm =>
{
    orm.ConnectionSecretName = "sales-sql-rw";           // NOME do segredo, nunca a conexão
    orm.ReadOnlyConnectionSecretName = "sales-sql-ro";   // login só com SELECT
});

builder.Services.AddHealthChecks().AddTecOrm();          // tags "ready" e "database"
```

```csharp
using Microsoft.EntityFrameworkCore;
using TEC.ORM.SqlServer.SoftDelete;

public sealed class SalesContext(DbContextOptions<SalesContext> options) : OrmDbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
}
```

> [!WARNING]
> Não use `EnableRetryOnFailure` do EF Core no `AddTecOrm`: quebra as transações do `IUnitOfWork` e repetiria escritas. As
> leituras já têm novas tentativas seguras (`TransientRetryCount`).

## 📚 Documentação

Registro, conexão e política, health check, opções, resiliência e observabilidade:
[docs/README.md](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/README.md).

`net8.0` e `net10.0` · EF Core 8/10 · [MIT](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/LICENSE) · Roberto Oliveira, equipe Tudo em Código
