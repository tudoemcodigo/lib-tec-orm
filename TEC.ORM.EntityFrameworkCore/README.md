<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# TEC.ORM.EntityFrameworkCore

Motor de persistência base e agnóstico da família **TEC.ORM**, integrando **Entity Framework Core** e **Dapper**.

## O que fornece

- **Registro comum (`AddTecOrmDbContext`)**: o pipeline que os quatro provedores usam: conexão do cofre, interceptores (conexão, exclusão lógica, autoria, trilha histórica), identidade da auditoria pelo `ICurrentUser`, convenções, logs sem dados sensíveis, `CommandTimeout` e mapeador de exceções do `TEC.Cqrs`.
- **Repositório Genérico (`OrmRepository<TEntity, TKey>`)**: Implementação das operações de CRUD, paginação, exclusão lógica propagada e exclusão física com log de auditoria (3007); em entidade `[Auditable]`, a exclusão física também gera `OrmAuditRecord` (`Deleted`).
- **Consultas Rápidas (`OrmQueryExecutor`)**: Execução de SQL de alta performance com Dapper, limite estrito de linhas (`MaxQueryRows`), cancelamento de comandos, transação `READ ONLY` nos bancos que a têm e o prefixo de parâmetro do dialeto.
- **Trilha Histórica de Auditoria (`HistoricalAuditInterceptor`)**: Gravação atômica de diffs (`OldValues` / `NewValues`) em JSON para entidades `[Auditable]`, com mascaramento `[SensitiveData]`, exclusão `[AuditIgnore]`, limite por valor (`MaxAuditValueLength`) e a chave real das inclusões com chave gerada pelo banco.
- **Idempotência no Banco (`EfIdempotencyStore`)**: Armazenamento e concorrência para chaves de idempotência integradas ao `TEC.Cqrs`, com o SQL atômico e a collation binária de cada banco.
- **Segurança de Conexões (`OrmConnectionSecurity`)**: Resolução de credenciais via `TEC.Vault`, validação de TLS mandatória, sanitização de strings e Circuit Breaker.
- **Abstrações de Dialeto (`ISqlDialect`, `IReadOnlySqlValidator`, `IOrmExceptionTranslator`, `OrmExceptionTranslatorBase`)**: Contratos estáveis para especialização por banco de dados.

## Instalação

```bash
dotnet add package TEC.ORM.EntityFrameworkCore --version 0.0.1
```

Este pacote é consumido automaticamente pelos provedores `TEC.ORM.SqlServer`, `TEC.ORM.PostgreSql`, `TEC.ORM.MySql` e `TEC.ORM.Oracle`.
O repositório, o executor Dapper, o runner, as opções e os interceptores são deste pacote nos quatro provedores; cada um traz
só o dialeto, o tradutor de exceções, o validador de leitura e o registro.

`net10.0` · EF Core 10 · [Apache 2.0](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/LICENSE) · Roberto Oliveira, equipe Tudo em Código
