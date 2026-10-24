# 📝 Changelog

Todas as mudanças relevantes do **TEC.ORM** são registradas aqui. O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa [Versionamento Semântico](https://semver.org/lang/pt-BR/).

Os pacotes da família `TEC.ORM` são lançados de forma sincronizada na mesma versão:
- `TEC.ORM`: Contratos, abstrações de repositório, consultas, paginação, mapeamento de DTOs e analisadores de código (Roslyn Source Generator integrado).
- `TEC.ORM.EntityFrameworkCore`: Núcleo agnóstico de EF Core e Dapper, repositório base, interceptors de exclusão lógica, auditoria histórica com diffs e motor de resiliência.
- `TEC.ORM.SqlServer`: Provedor Microsoft SQL Server / Azure SQL.
- `TEC.ORM.PostgreSql`: Provedor PostgreSQL (Npgsql).
- `TEC.ORM.MySql`: Provedor MySQL / MariaDB (MySqlConnector e compatibilidade Oracle MySQL).
- `TEC.ORM.Oracle`: Provedor Oracle Database (Oracle.ManagedDataAccess).

---

## [0.0.1] - 2026-10-24

Primeira versão unificada do componente, pronta para produção e totalmente homologada.

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
- **Resiliência e Observabilidade Integrada**: Métricas de duração (`orm.operation.duration`), contadores de erro e traces OpenTelemetry (`ActivitySource TEC.ORM`) padronizados sem vazamento de dados confidenciais ou valores SQL.
- **Trilha de Auditoria Histórica (Audit Trail)**:
  - Gravação automática de diffs (`OldValues`, `NewValues`, `AffectedColumns`) em formato JSON para entidades anotadas com `[Auditable]`.
  - Mascaramento de propriedades sensíveis com `[SensitiveData]` e exclusão com `[AuditIgnore]`.
  - Rastreamento contextual via `IOrmAuditContextProvider`: suporte a atores `User`, `M2M` (Machine-to-Machine / Client Credentials), `ApiKey`, `System` e `Anonymous`, além de IP, TraceId e CorrelationId.
  - Mapeamento otimizado de tabela e índices de consulta via `modelBuilder.AddTecOrmHistoricalAudit()`.
- **Exclusão Lógica Automática (Soft Delete)**:
  - Suporte a `ISoftDelete` com preenchimento transparente de `IsDeleted`, `DeletedAt` e `DeletedBy`.
  - Filtro global de consulta automático para entidades excluídas no EF Core 8 e 10.
  - Propagação segura em relacionamentos com teto máximo configurável (`MaxSoftDeletePropagation`).
- **Idempotência no Banco de Dados**:
  - Implementação `EfIdempotencyStore<TContext>` compatível com `IIdempotencyStore` do `TEC.Cqrs`.
  - Reserva atômica de chaves e expurgo periódico de registros expirados (`IdempotencyCleanupService`).
- **Segurança de Conexão com Cofre (`IOrmConnectionSecurity`)**:
  - Resolução transparente de credenciais via `TEC.Vault`.
  - Criptografia de trânsito mandatória (TLS/SSL obrigatório em todos os dialetos).
  - Objeto seguro `ProtectedConnectionString` que oculta segredos de logs, depuradores e inspeção de memória acidental.
  - Circuit Breaker nativo para abertura de conexões integrado com Polly.

#### 🌐 Provedores de Banco de Dados Homologados
- **`TEC.ORM.SqlServer`**: Provedor oficial SQL Server / Azure SQL com `AddTecOrmSqlServer` / `AddTecOrm`, dicas de bloqueio (`UPDLOCK, HOLDLOCK`), isolamento de leitura com `ApplicationIntent=ReadOnly` e tratamento especializado de exceções SqlClient.
- **`TEC.ORM.PostgreSql`**: Provedor oficial PostgreSQL com `AddTecOrmPostgreSql`, isolamento de transação ambiente (`Enlist=false`), inserção atômica com `ON CONFLICT DO NOTHING`, sanitização com `SslMode` estrito e bloqueio de funções do sistema (`pg_*`, `dblink`, `lo_*`).
- **`TEC.ORM.MySql`**: Provedor oficial MySQL / MariaDB com `AddTecOrmMySql`, suporte híbrido a `MySqlConnector` e drivers Oracle, inserção atômica com `INSERT IGNORE`, bloqueio de funções perigosas (`LOAD_FILE`, `SLEEP`) e comentários executáveis (`/*! ... */`).
- **`TEC.ORM.Oracle`**: Provedor oficial Oracle Database com `AddTecOrmOracle`, inserção atômica via `MERGE INTO`, sanitização de conexões TCPS seguras e bloqueio de pacotes de sistema (`DBMS_*`, `UTL_*`).

#### 🧪 Suíte de Testes e Garantia de Qualidade
- 916 testes unitários automatizados em matriz dupla (.NET 8 e .NET 10 LTS) com 100% de aprovação.
- Testes adversariais de Red Team cobrindo Fuzzing diferencial, injeção de SQL, DoS por parâmetros, tentativa de desvio de leitura, concorrência e mitigação de vazamento de credenciais.
