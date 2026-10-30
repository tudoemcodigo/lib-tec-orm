# 📝 Changelog

Todas as mudanças relevantes do **TEC.ORM** são registradas aqui. O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa [Versionamento Semântico](https://semver.org/lang/pt-BR/).

Os pacotes da família `TEC.ORM` são lançados de forma sincronizada na mesma versão:
- `TEC.ORM`: Contratos, abstrações de repositório, consultas, paginação, mapeamento de DTOs e analisadores de código (Roslyn Source Generator integrado).
- `TEC.ORM.EntityFrameworkCore`: Núcleo agnóstico de EF Core e Dapper, repositório base, interceptors de exclusão lógica, auditoria histórica com diffs e motor de resiliência.
- `TEC.ORM.SqlServer`: Provedor Microsoft SQL Server / Azure SQL.
- `TEC.ORM.PostgreSql`: Provedor PostgreSQL (Npgsql).
- `TEC.ORM.MySql`: Provedor MySQL / MariaDB (`MySql.EntityFrameworkCore` e o driver MySql.Data).
- `TEC.ORM.Oracle`: Provedor Oracle Database (`Oracle.EntityFrameworkCore` e o driver Oracle.ManagedDataAccess.Core).

---

## [0.0.1] - 2026-10-30

Primeira versão unificada do componente. Os pacotes têm um só alvo, `net10.0` (EF Core 10), sem polyfills. Os quatro provedores usam o pipeline do `TEC.ORM.EntityFrameworkCore` e o mesmo registro. O SQL Server tem testes de integração com o banco real; PostgreSQL, MySQL e Oracle têm testes unitários dos dialetos, de registro e de integração, estes ainda não executados contra os bancos reais (ver [README](README.md#-compatibilidade)).

### ✨ Adicionado

#### 📦 TEC.ORM (Abstrações e Source Generator AOT-Ready)
- **`IOrmRepository<TEntity, TKey>`**: CRUD genérico completo (`CreateAsync`, `GetByIdAsync`, `UpdateAsync`, `DeleteAsync`, `FindAsync`, `ListAsync`, `ExistsAsync`, `CountAsync`) sempre retornando contratos de `Result` do `TEC.Core`.
- **Projeção de DTOs em tempo de compilação**: Métodos `<TDto>` com projeção otimizada no banco via Source Generator (sem reflexão em runtime, 100% Native AOT).
- **Exclusão Física Explícita**: `HardDeleteAsync` por ID ou por especificação em lote (`ISpecification<TEntity>`), com verificação de segurança `[HardDelete]` e auditoria.
- **Consultas Rápidas e Seguras (`IOrmQueryExecutor`)**: Execução de leitura rápida via Dapper com proteção contra SQL Injection e validação estrita de somente leitura.
- **`SqlQuery`**: Construção tipada e parametrizada com controle de volume de parâmetros, sanitização de curingas (`SqlQuery.EscapeLike`) e suporte a múltiplos mapeamentos (`splitOn`).
- **Paginação e Ordenação**: `PageRequest` com proteção de offset máximo (`MaxPageOffset`) e ordenação segura por atributo `[Sortable]`.
- **Analisadores Roslyn (`TECORM001` a `TECORM016`)**: Diagnósticos em tempo de compilação para consistência de DTOs, mapeamento, prevenção de ciclos e aviso de exclusão física.

#### 🏛️ TEC.ORM.EntityFrameworkCore (Motor Base de Persistência)
- **Registro comum** (`AddTecOrmDbContext`), usado pelos quatro provedores: identidade da auditoria pelo `ICurrentUser` do escopo (`UseTecOrmIdentity`); interceptores na ordem conexão → exclusão lógica → autoria → trilha histórica; convenções do TEC.ORM; `EnableSensitiveDataLogging(false)` e os eventos de erro do EF Core que repetiriam a mensagem do banco desligados; `CommandTimeout` aplicado aos comandos do EF Core; um `IExceptionErrorMapper` do TEC.Cqrs por dialeto; e `DefaultOrmAuditContextProvider` com o `ICurrentUser` (sem um provedor registrado pela aplicação, a trilha não grava `Anonymous` para tudo). O dialeto do provedor registrado substitui o `OrmOptions.Dialect` definido no `configure`.
- **Resiliência e Observabilidade Integrada**: Métricas de duração (`orm.operation.duration`), contadores de erro e traces OpenTelemetry (`ActivitySource TEC.ORM`) padronizados sem vazamento de dados confidenciais ou valores SQL. `db.system.name` vem do dialeto (`ISqlDialect.DbSystemName`, `microsoft.sql_server` no SQL Server).
- **Abertura da conexão**: cancelamento pedido pelo chamador volta como `OperationCanceledException` (não vira `ORM_CONEXAO_INDISPONIVEL`); erro de programação não é disfarçado de indisponibilidade; o log 3102 traz o tipo da exceção e o código de erro do banco.
- **Tradução de erros**: a exceção do driver é procurada nas `InnerException`; `DbUpdateConcurrencyException` vira concorrência; as recusas do próprio ORM são reconhecidas.
- **`IOrmQueryExecutor`**: sem verificação de somente leitura para o banco, nenhuma consulta passa (`ORM_CONSULTA_NAO_PERMITIDA`). No PostgreSQL, MySQL e Oracle a consulta roda numa transação `READ ONLY`, segunda barreira depois do validador. No Oracle, os marcadores do `SqlQuery.Interpolated` saem como `:p0`.
- **Trilha de Auditoria Histórica (Audit Trail)**:
  - Gravação automática de diffs (`OldValues`, `NewValues`, `AffectedColumns`) em formato JSON para entidades anotadas com `[Auditable]`.
  - Mascaramento de propriedades sensíveis com `[SensitiveData]` e exclusão com `[AuditIgnore]`.
  - Rastreamento contextual via `IOrmAuditContextProvider`: suporte a atores `User`, `M2M` (Machine-to-Machine / Client Credentials), `ApiKey`, `System` e `Anonymous`, além de IP, TraceId e CorrelationId.
  - Mapeamento otimizado de tabela e índices de consulta via `modelBuilder.AddTecOrmHistoricalAudit()`. Em `AddTecOrmHistoricalAudit` e `ApplyAuditTrailConfiguration`, o padrão de `tableName` é `null` (vale `OrmOptions.AuditLogTableName`, que nomeia a tabela quando o modelo não a nomeia).
  - A trilha roda depois da autoria e registra os valores carimbados (`CreatedBy`, `DeletedBy`...), não os que o chamador enviou. A exclusão lógica preenche `DeletedBy` e gera `SoftDeleted`.
  - Inclusão com chave gerada pelo banco é registrada depois do `INSERT`, na mesma transação (aberta pelo interceptor se não houver uma), já com a chave real.
  - `HardDeleteAsync` de entidade `[Auditable]` grava `Deleted` com os valores antigos; por especificação, lê até `MaxFindResults` linhas e as remove uma a uma (acima disso, `ORM_LIMITE_EXCEDIDO` sem excluir nada).
  - Cada valor é cortado em `OrmOptions.MaxAuditValueLength` (padrão 1024) e binários viram `<N bytes>`.
- **Exclusão Lógica Automática (Soft Delete)**:
  - Suporte a `ISoftDelete` com preenchimento transparente de `IsDeleted`, `DeletedAt` e `DeletedBy`.
  - Filtro global de consulta automático para entidades excluídas no EF Core.
  - Propagação segura em relacionamentos com teto máximo configurável (`MaxSoftDeletePropagation`).
  - O índice de `HasSoftDelete` recebe o filtro na sintaxe do banco (`[IsDeleted] = 0` no SQL Server, `"IsDeleted" = FALSE` no PostgreSQL); MySQL e Oracle ficam sem filtro.
- **Idempotência no Banco de Dados**:
  - Implementação `EfIdempotencyStore<TContext>` compatível com `IIdempotencyStore` do `TEC.Cqrs`. Sem dialeto, a construção falha com `InvalidOperationException`, em vez de gerar SQL do SQL Server para qualquer banco. O contexto registrado por `AddTecOrm*` já traz o dialeto; fora dele, use o construtor que recebe `ISqlDialect`.
  - Reserva atômica de chaves e expurgo periódico de registros expirados (`IdempotencyCleanupService`).
  - O SQL atômico usa os nomes de coluna do modelo, e só a violação de unicidade de cada banco conta como "chave já existe".
- **Segurança de Conexão com Cofre (`IOrmConnectionSecurity`)**:
  - Resolução transparente de credenciais via `TEC.Vault`.
  - `IOrmConnectionSecurity.OpenConnectionAsync` devolve `Result<DbConnection>`. Implementações próprias, como a de token do Entra ID, usam esse tipo de retorno.
  - Os construtores de `OrmConnectionSecurity` e `OrmOperationRunner` têm um parâmetro opcional antes do `TimeProvider` (`dialect` e `translator`): para passar o relógio, use `time:`.
  - Criptografia de trânsito exigida pela política do dialeto: SQL Server (`Encrypt`), PostgreSQL (`SslMode`), MySQL (`SslMode`) e Oracle (recusa `PROTOCOL=TCP` explícito e EZConnect sem `tcps://`; alias do `tnsnames` não é inspecionado).
  - Objeto seguro `ProtectedConnectionString` que oculta segredos de logs, depuradores e inspeção de memória acidental.
  - Circuit Breaker nativo para abertura de conexões integrado com Polly.

#### 🌐 Provedores de Banco de Dados
- **`TEC.ORM.SqlServer`**: Provedor SQL Server / Azure SQL com `AddTecOrmSqlServer` / `AddTecOrm`, `UseTecOrm`, `AddTecOrmIdempotency`, `AddHealthChecks().AddTecOrm()` (com `ReadyTag` e `DatabaseTag`, encaminhado para o health check do núcleo), dicas de bloqueio (`UPDLOCK, HOLDLOCK`), isolamento de leitura com `ApplicationIntent=ReadOnly` e tratamento especializado de exceções SqlClient no `SqlServerDialect` (`ExceptionTranslator`, do tipo público `SqlServerExceptionTranslator`). Não tem cópia própria do pipeline: o repositório, o executor Dapper e os interceptores são os do `TEC.ORM.EntityFrameworkCore`, dependência do pacote, e os tipos ficam nos namespaces do núcleo:

  | Tipos | Namespace |
  |---|---|
  | `OrmOptions`, `OrmCircuitBreakerOptions`, `IdentifierLogMode` | `TEC.ORM.EntityFrameworkCore.Configuration` |
  | `OrmIdentity` (`UseTecOrmIdentity`) | `TEC.ORM.EntityFrameworkCore.Auditing` |
  | `IOrmOperationRunner`, `OrmOperationRunner`, `OrmOperation`, `OrmDiagnostics` | `TEC.ORM.EntityFrameworkCore.Diagnostics` |
  | `EfIdempotencyStore<TContext>`, `IdempotencyModelBuilderExtensions`, `OrmIdempotencyOptions` | `TEC.ORM.EntityFrameworkCore.Idempotency` |
  | `IOrmConnectionSecurity`, `OrmConnectionKind`, `OrmConnectionSecurity` | `TEC.ORM.EntityFrameworkCore.Security` |
  | `OrmDbContext`, `OrmModelConfigurationExtensions` | `TEC.ORM.EntityFrameworkCore.SoftDelete` |
  | `OrmUnitOfWork` | `TEC.ORM.EntityFrameworkCore.UnitOfWork` |
  | `OrmRepository<TEntity, TKey>`, `OrmQueryExecutor` | `TEC.ORM.EntityFrameworkCore` |

- **`TEC.ORM.PostgreSql`**: Provedor PostgreSQL com `AddTecOrmPostgreSql`, `Enlist=false` na conexão de leitura, inserção atômica com `ON CONFLICT DO NOTHING` e collation binária `C` na idempotência, `SslMode` restrito (`Require` só com `AllowTrustServerCertificate`, porque no Npgsql 6 ou superior ele cifra sem validar o certificado) e validador somente leitura que recusa `PG_SLEEP`, `PG_READ_FILE`, `PG_READ_BINARY_FILE`, `PG_WRITE_FILE`, `PG_WRITE_BINARY_FILE`, `DBLINK`, `DBLINK_EXEC`, `LO_IMPORT`, `LO_EXPORT`, `SET_CONFIG`, `NEXTVAL`, `SETVAL`, `PG_ADVISORY_*`, `PG_TERMINATE_BACKEND`, `PG_CANCEL_BACKEND`, entre outras, e `FOR SHARE`.
- **`TEC.ORM.MySql`**: Provedor MySQL / MariaDB com `AddTecOrmMySql` (parâmetro opcional `mySql` para configurar o provedor do EF Core). O EF Core usa o `MySql.EntityFrameworkCore` e o Dapper usa o mesmo driver, o MySql.Data (sem MySqlConnector nem Pomelo): as exceções são `MySql.Data.MySqlClient.MySqlException`, e `OrmOptions.ApplicationName` não é aplicado (o MySql.Data não tem a chave). `AutoEnlist=false` na conexão de leitura e `AllowLoadLocalInfile=false`; idempotência com `INSERT` simples e collation `utf8mb4_bin` (pelas anotações do `MySql.EntityFrameworkCore`, que ignora a collation relacional); validador que recusa `LOAD_FILE`, `SLEEP`, `BENCHMARK`, `FOR SHARE`, `GET_LOCK` e as demais funções de trava e comentários executáveis (`/*! ... */`, `/*M! ... */`), e aceita `REPLACE()` e `TRUNCATE()` usados como funções.
- **`TEC.ORM.Oracle`**: Provedor Oracle Database (`Oracle.EntityFrameworkCore`) com `AddTecOrmOracle`, inserção atômica via `MERGE INTO` com o corpo da resposta em `BLOB` (o padrão do provedor, `RAW(2000)`, não guardaria respostas maiores) e valores da trilha (`OldValues`, `NewValues`, `AffectedColumns`) em `NCLOB` (com o padrão `NVARCHAR2(2000)`, uma alteração maior faria o salvamento falhar). Recusa `PROTOCOL=TCP` explícito e EZConnect sem `tcps://`; descritores com tabulação ou quebra de linha são normalizados antes da verificação; `SSL_SERVER_DN_MATCH` desligado só passa com `AllowTrustServerCertificate`. O validador recusa nomes que começam com `DBMS_` ou `UTL_` avaliando cada parte do nome (inclusive `SYS.DBMS_*`, `SYS.UTL_*` e nomes entre aspas). A conexão de leitura recebe `Enlist=false`, e o tempo limite de comando não altera o `Connection Timeout`.

#### 🧪 Suíte de Testes e Garantia de Qualidade
- Testes unitários em .NET 10; a contagem sai do `dotnet test`. Testes de integração com banco real no CI só para o SQL Server.
- Registro de cada provedor no container real, pipeline comum (abertura de conexão, transação somente leitura, limite da trilha, convenções e SQL de modelo por banco) e casos adversariais dos validadores e da política de TLS do PostgreSQL, MySQL e Oracle.
- Testes de integração por banco (`ProviderIntegrationTests`), pulados sem a conexão: `TEC_TESTES_ORM_PG_CONEXAO`, `TEC_TESTES_ORM_MYSQL_CONEXAO` e `TEC_TESTES_ORM_ORACLE_CONEXAO` (o SQL Server usa a conexão dos demais testes). Ainda não foram executados contra PostgreSQL, MySQL ou Oracle reais.
- Testes adversariais cobrindo fuzzing diferencial (só T-SQL), injeção de SQL, DoS por parâmetros, tentativa de desvio de leitura, concorrência e vazamento de credenciais. PostgreSQL, MySQL e Oracle têm casos de bypass do validador em `DialectSecurityTests`.
