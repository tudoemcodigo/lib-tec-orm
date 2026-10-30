[🏠 TEC.ORM](../README.md) › [📚 Documentação](README.md) › 🔍 Revisão técnica

# 🔍 Revisão técnica

> Revisão de segurança, correção e organização do TEC.ORM feita em **2026-10-19**, com cada achado conferido no código-fonte
> (arquivo e linha). Não altera o código: registra o que foi encontrado, o impacto e a correção sugerida.

> [!NOTE]
> Relatório histórico de 2026-10-19: não trate os resultados como o estado atual. Ele cobre só o `TEC.ORM`, o
> `TEC.ORM.SqlServer` e o gerador (não cobre `TEC.ORM.EntityFrameworkCore`, `PostgreSql`, `MySql` nem `Oracle`). As versões
> `0.1.0` e `0.2.0` citadas aqui não correspondem à `<Version>` atual do `Directory.Build.props` (`0.0.1`).

## 📑 Sumário

- [🎯 Escopo](#-escopo)
- [🧪 Build e testes](#-build-e-testes)
- [📋 Resumo dos achados](#-resumo-dos-achados)
- [✅ Status por achado](#-status-por-achado)
- [🛠️ Correções aplicadas — 2026-10-19](#️-correções-aplicadas--2026-10-19)
- [🔎 Achados em detalhe](#-achados-em-detalhe)
  - [Alta](#alta)
  - [Média](#média)
  - [Baixa](#baixa)
  - [Info](#info)
- [✨ Melhorias sugeridas](#-melhorias-sugeridas)
- [📝 Ajustes na documentação](#-ajustes-na-documentação)

---

## 🎯 Escopo

| Item | Valor |
|---|---|
| Data | 2026-10-19 |
| Commit analisado | `9c3152e Circuit breaker na abertura de conexões e dependências estáveis (0.1.0) (#10)` |
| Projetos | `TEC.ORM` (abstrações, `SqlQuery`, especificações, paginação, mapeamento), `TEC.ORM.SqlServer` (EF Core, Dapper, conexão, exclusão lógica, auditoria, idempotência, resiliência, observabilidade), `TEC.ORM.Mapping.Generator` (gerador e analisador `TECORM014`), testes, benchmarks, carga, arquivos `.sql`, `build/` e `.github/` |
| Foco | Injeção de SQL (identificadores, `ORDER BY` dinâmico, especificações, `LIKE`, `IN`), gerador e diagnósticos, CRUD, paginação, exclusão lógica, auditoria, idempotência, transação, resiliência, SQL Server, vazamento em logs/traces, build e documentação |
| Fora do escopo | Código dos componentes irmãos (TEC.Core, TEC.Cqrs, TEC.Vault, TEC.Observability), consultados só para entender a integração |

O que foi conferido e **não** gerou achado: o CRUD é 100% LINQ (valores sempre parametrizados, inclusive o `HasId` por
closure); `SortBy` vira `EF.Property` com o nome **do modelo** (o texto do usuário nunca chega ao SQL); `SqlQuery` não aceita
texto solto e valida nome, tamanho e nomes de parâmetro; a política da string de conexão (`Encrypt`, `TrustServerCertificate`,
`Persist Security Info=False`) e o `ProtectedConnectionString`; a tradução de exceções sem mensagem do banco; o
`ReadOnlySqlGuard` contra `;`, comentários, literais e o truque `1DROP`; o cancelamento do comando no servidor ao estourar
`MaxQueryRows`; a reserva atômica da idempotência (`UPDLOCK, HOLDLOCK` + `LockId`) com escopo pelo usuário autenticado
(TEC.Cqrs); a proteção dos campos de autoria e de exclusão lógica no `UPDATE` e no `ApplyTo` gerado; o escape de palavras-chave
C# no código gerado; e a ausência de identificador, SQL e valores em traces e métricas.

---

## 🧪 Build e testes

| Passo | Comando | Resultado |
|---|---|---|
| Build padrão (feed `tec-interno`) | `dotnet build TEC.ORM.slnx -c Release` | ❌ `NU1301` (401 no GitHub Packages): sem credencial do feed nesta máquina |
| Build com os repositórios vizinhos | `dotnet build ... -p:TecUseLocalProjects=true` | ❌ `NU1903`/`NU1902` como erro: `System.Security.Cryptography.Pkcs` 9.0.11 vulnerável (ver [ORG-01](#org-01)) |
| Build sem a auditoria NuGet (só para obter evidência) | `dotnet build TEC.ORM.slnx -p:TecUseLocalProjects=true -p:NuGetAudit=false` | ✅ 0 avisos, 0 erros |
| Unitários net10.0 | `dotnet run -f net10.0 -- --treenode-filter "/*/*/*/*[Category!=Integracao]"` | ✅ 316 de 316 |
| Unitários net8.0 | idem com `-f net8.0` | ✅ 314 de 314 |
| Integração e carga | — | Não executados (precisam de SQL Server em container e do cofre) |

---

## 📋 Resumo dos achados

| Severidade | Quantidade |
|---|:-:|
| Crítica | 0 |
| Alta | 1 |
| Média | 5 |
| Baixa | 10 |
| Info | 4 |

| ID | Severidade | Categoria | Local | Resumo |
|---|---|---|---|---|
| [ORG-01](#org-01) | Alta | Build / dependências | `TEC.ORM.SqlServer/packages.lock.json:120,365` | `System.Security.Cryptography.Pkcs` 9.0.11 (transitivo do SqlClient no net10.0) tem vulnerabilidade alta e moderada: o build quebra e o pacote publicado a carrega |
| [SEG-01](#seg-01) | Média | Vazamento em log | `Diagnostics/OrmOperationRunner.cs:240`, `OrmQueryExecutor.cs:174` | O log 3005 grava a mensagem de exceções de conversão, que trazem o **valor da coluna** (`FormatException` do .NET 8+, `DataException` do Dapper) |
| [SEG-02](#seg-02) | Média | Auditoria | `OrmRepository.cs:167-202`, `Internal/OrmLog.cs:42-44` | Exclusão física sem autoria: o log 3007 não diz **quem** excluiu nem **quantas** linhas saíram no lote |
| [SEG-03](#seg-03) | Média | Ordenação dinâmica | `OrmRepository.cs:402-422` | Sem `[Sortable]`, o `SortBy` vindo da requisição aceita qualquer coluna escalar (hash, documento, `CreatedBy`): falha aberta por padrão |
| [BUG-01](#bug-01) | Média | Transação | `Security/OrmConnectionSecurity.cs:191-199`, `OrmQueryExecutor.cs:98-112` | A conexão do Dapper se alista no `TransactionScope` ambiente: promoção a transação distribuída (falha) e nova tentativa dentro da transação |
| [BUG-02](#bug-02) | Média | Exclusão lógica | `SoftDelete/SoftDeleteInterceptor.cs:103-110,156-170` | A propagação carrega **todos** os dependentes na memória, com uma consulta por entidade (N+1) e varredura O(n²) do rastreador |
| [SEG-04](#seg-04) | Baixa | SQL somente leitura | `Internal/ReadOnlySqlGuard.cs:266-268`, `OrmQueryExecutor.cs:128-139` | Vários `SELECT` sem `;` passam; o segundo resultado é drenado ao fechar o leitor, fora do `MaxQueryRows` |
| [SEG-05](#seg-05) | Baixa | `IN` / `LIKE` | `Queries/SqlQuery.cs:65,107`, `OrmQueryExecutor.cs:119-121` | Lista em parâmetro é expandida pelo Dapper e escapa do limite de 2.000 parâmetros; não há ajuda para escapar curingas de `LIKE` |
| [SEG-06](#seg-06) | Baixa | Log / LGPD | `Configuration/OrmOptions.cs:93` | `IdentifierLogMode.Plain` é o padrão: chave `string` (e-mail, CPF) vai em claro para o log |
| [BUG-03](#bug-03) | Baixa | CRUD | `OrmRepository.cs:102-117` | Corrida no `UpdateAsync`: uma exclusão lógica entre o `AnyAsync` e o `UPDATE` não impede a atualização |
| [BUG-04](#bug-04) | Baixa | Auditoria | `OrmRepository.cs:118`, `Auditing/AuditInterceptor.cs:121-129` | `UpdateAsync` devolve os campos de autoria **enviados pelo chamador**, não os gravados |
| [BUG-05](#bug-05) | Baixa | CRUD | `OrmRepository.cs:57-60` | `CreateAsync` zera a exclusão lógica só da raiz: dependentes novos no grafo podem nascer excluídos |
| [BUG-06](#bug-06) | Baixa | Paginação | `Paging/PageRequest.cs:20-31`, `OrmRepository.cs:280-288` | `OFFSET` profundo sem teto e `COUNT` a cada página: custo controlado pelo cliente |
| [IMP-01](#imp-01) | Baixa | Analisador | `HardDeleteUsageAnalyzer.cs:241-251` | `TECORM014` não alerta quando `HardDeleteAsync` é usado como *method group* (delegate) |
| [IMP-02](#imp-02) | Baixa | Modelo SQL Server | `SoftDelete/SoftDeleteQueryFilterConvention.cs:207` | `HasSoftDelete` cria índice só sobre o `bit` (quase nunca usado) e com o nome da coluna fixo no filtro |
| [DOC-01](#doc-01) | Baixa | Documentação | `opcoes.md`, `observabilidade.md`, `resiliencia.md` | Linhas de tabela soltas antes do breadcrumb (corrigido nesta revisão) |
| [IMP-03](#imp-03) | Info | Desempenho | `OrmRepository.cs:404-408` | A lista branca de ordenação é recalculada (LINQ + reflexão de atributo) a cada listagem |
| [IMP-04](#imp-04) | Info | SQL Server | `OrmQueryExecutor.cs:119-121` | `string` vai como `nvarchar`: `CONVERT_IMPLICIT` em colunas `varchar` impede *seek* |
| [ORG-02](#org-02) | Info | Testes | `TEC.ORM.Tests` | Sem testes para os cenários de BUG-01, BUG-02, SEG-01, SEG-02 e SEG-04 |
| [DOC-02](#doc-02) | Info | Documentação | `observabilidade.md`, `consultas-sql.md` | Texto dos eventos 3200/3201, categorias de log e lacunas de `consultas-sql.md` (corrigido nesta revisão) |

---

## ✅ Status por achado

Arquivos sem caminho completo estão em `TEC.ORM.SqlServer/`; testes em `TEC.ORM.Tests/`.

| ID | Status | Arquivos alterados | Teste de regressão |
|---|---|---|---|
| ORG-01 | ✅ Corrigido (lock files pendentes, ver abaixo) | `Directory.Packages.props` (`System.Security.Cryptography.Pkcs` 10.0.13 no `net10.0`) | Build com auditoria NuGet ligada |
| SEG-01 | ✅ Corrigido | `Diagnostics/OrmOperationRunner.cs` (`OrmRedactedException`) | `ObservabilityTests.Conversion_exception_is_logged_without_the_column_value`; integração `LeakageTests.ColumnTextReadAsNumber_ColumnValue_NeverLeaks` |
| SEG-02 | ✅ Corrigido | `OrmRepository.cs`, `Internal/OrmLog.cs`, `Diagnostics/OrmOperationRunner.cs` (`Actor`, `AffectedRows`) | `ObservabilityTests.Hard_delete_audit_carries_actor_and_row_count`, `RevisionRegressionTests.Hard_delete_of_auditable_entity_without_identity_is_rejected`; integração `RevisionIntegrationTests.Hard_delete_audit_log_has_actor_and_rows` |
| SEG-03 | ✅ Corrigido (⚠️ comportamento) | `OrmRepository.cs`, `Configuration/OrmOptions.cs` (`AllowUnmarkedSortBy`) | `AggregateTests.Without_Sortable_only_the_key_is_accepted_by_default` |
| BUG-01 | ✅ Corrigido | `Security/OrmConnectionSecurity.cs` (`Enlist=false` na leitura), `OrmQueryExecutor.cs` (doc) | `SecurityTests` (asserts de `Enlist`); integração `RevisionIntegrationTests.Query_executor_inside_transaction_scope_with_the_repository_is_not_promoted` |
| BUG-02 | ✅ Corrigido (mitigação: teto + índice; a propagação continua em memória) | `SoftDelete/SoftDeleteInterceptor.cs`, `Configuration/OrmOptions.cs` (`MaxSoftDeletePropagation`), `DependencyInjection/ServiceCollectionExtensions.cs`, `Internal/OrmExceptionTranslator.cs`, `TEC.ORM/Common/OrmErrors.cs` | `RevisionRegressionTests.Propagation_*` (3) |
| SEG-04 | ✅ Corrigido | `OrmQueryExecutor.cs` (`NextResultAsync`), `Internal/ReadOnlySqlGuard.cs` (dicas de bloqueio) | `QueryTests` (4 casos novos); integração `RevisionIntegrationTests.Second_result_set_is_rejected_without_being_read` |
| SEG-05 | ✅ Corrigido | `TEC.ORM/Queries/SqlQuery.cs` (contagem de itens, `EscapeLike`) | `RevisionRegressionTests.List_items_count_toward_the_parameter_limit`, `Escape_like_neutralizes_wildcards` |
| SEG-06 | ✅ Corrigido (⚠️ comportamento) | `Configuration/OrmOptions.cs` (`IdentifierLogMode.Auto`), `Diagnostics/OrmOperationRunner.cs` | `ObservabilityTests.Auto_mode_hashes_text_actor_and_text_identifier`, `FuzzingTests` (Auto) |
| BUG-03 | ✅ Corrigido | `SoftDelete/SoftDeleteQueryFilterConvention.cs` (`IsDeleted` token de concorrência) | `RevisionRegressionTests.Concurrent_soft_delete_between_check_and_update_is_concurrency_and_keeps_the_record` |
| BUG-04 | ✅ Corrigido | `OrmRepository.cs` (`AuditSnapshot`) | `RevisionRegressionTests.Update_returns_the_stored_authorship_not_the_one_sent` |
| BUG-05 | ✅ Corrigido | `OrmRepository.cs` (`CreateAsync`) | `RevisionRegressionTests.Create_never_inserts_graph_dependents_already_deleted` |
| BUG-06 | ✅ Corrigido (⚠️ comportamento; paginação por chave não implementada) | `OrmRepository.cs`, `Configuration/OrmOptions.cs` (`MaxPageOffset`) | `DosResistanceTests` (cenário novo) |
| IMP-01 | ✅ Corrigido | `TEC.ORM.Mapping.Generator/HardDeleteUsageAnalyzer.cs` | `HardDeleteAnalyzerTests.Method_group_raises_the_warning` |
| IMP-02 | ✅ Corrigido em parte | `SoftDelete/SoftDeleteQueryFilterConvention.cs` (filtro com `GetColumnName`) | `RevisionRegressionTests.Soft_delete_index_filter_uses_the_mapped_column_name` |
| DOC-01 | ✅ Corrigido na revisão | — | — |
| IMP-03 (Info) | ✅ Corrigido | `OrmRepository.cs` (`SortWhitelist` por modelo, `FrozenDictionary`) | coberto pelos testes de ordenação |
| IMP-04 (Info) | ❌ Não corrigido | — | Exige nova API em `SqlQuery` (`DbString`/`AnsiString`): fora do escopo de correção |
| ORG-02 (Info) | ✅ Corrigido | testes acima | — |
| DOC-02 (Info) | ✅ Corrigido na revisão | — | — |

**IMP-02 em parte:** o padrão `HasSoftDelete(index: true)` foi mantido, porque trocá-lo geraria uma migration de remoção do
índice em todo consumidor; o bug do nome da coluna foi corrigido e o padrão de índice útil, documentado.
**BUG-02:** a propagação no banco (`ExecuteUpdate` por nível) não foi feita: exigiria refazer a auditoria (`DeletedBy`) fora
do rastreador e não roda no provedor InMemory usado pelos testes. O teto limita memória e tempo (cada carga lê no máximo o que
cabe no teto) e o índice elimina a varredura O(n²); o N+1 continua, documentado em [🗑️ Exclusão lógica](exclusao-logica.md).

---

## 🛠️ Correções aplicadas — 2026-10-19

Branch `fix/revisao-tecnica-2026-10-19`; versão **0.2.0** (MINOR em 0.x: SEG-02, SEG-03, SEG-06, BUG-02 e BUG-06 mudam
comportamento; ver [📝 Changelog](../CHANGELOG.md)). Também aplicados os itens deste repositório da revisão do tec-workflows:
imagem do SQL Server fixada por digest em `.github/scripts/integration-setup.sh` (B9) e `TEC.Vault` 0.0.1 → 0.1.0 (I3).
A pedido do TEC.Cqrs (B-04, comparação ordinal no `IIdempotencyStore`): colunas `Scope`/`IdempotencyKey` com collation
`Latin1_General_100_BIN2`, com script de migração em [🔁 Idempotência](idempotencia.md) (a coluna `Scope`, `nvarchar(200)`,
comporta o hash SHA-256 hex de 64 caracteres do TEC.Cqrs 0.2.0).

| Passo | Comando | Resultado |
|---|---|---|
| Build com os repositórios vizinhos, **com** a auditoria NuGet | `dotnet build TEC.ORM.slnx -c Release -p:TecUseLocalProjects=true` | ✅ 0 avisos, 0 erros |
| Unitários net10.0 | `dotnet run -c Release -f net10.0 -- --treenode-filter "/*/*/*/*[Category!=Integracao]"` | ✅ 347 de 347 |
| Unitários net8.0 | idem com `-f net8.0` | ✅ 345 de 345 |
| Integração (SQL Server 2022 em container local) | `[Category=Integracao]`, net10.0 | ✅ 51 de 52 (1 pulado: Key Vault) — executada antes da mudança de collation da idempotência; depois dela, não reexecutada localmente (roda no CI) |
| Build padrão (feed `tec-interno`) e lock files | `dotnet restore TEC.ORM.slnx --force-evaluate` | ⛔ Bloqueado: `NU1301` (401) sem credencial. Os `packages.lock.json` **não** foram regenerados (Pkcs 10.0.13 e TEC.Vault 0.1.0 ainda não estão neles): com o token, rode o comando acima e versione os lock files antes do PR, ou o CI (`RestoreLockedMode`) falha |

---

## 🔎 Achados em detalhe

### Alta

<a id="org-01"></a>
#### ORG-01 · Dependência transitiva vulnerável quebra o build (Alta)

**Problema.** No `net10.0`, o EF Core 10.0.12 traz `Microsoft.Data.SqlClient` 6.1.6, que depende de
`System.Security.Cryptography.Pkcs` 9.0.11. Esse pacote tem os avisos GHSA-846f-f764-76mx (alta) e GHSA-2fmv-rv2c-59x7
(moderada). Como o `build/Tec.Build.props:55` trata `NU1901`–`NU1904` como erro, o restore falha.

**Evidência.**

```text
TEC.ORM.SqlServer/packages.lock.json:120   "System.Security.Cryptography.Pkcs": "9.0.11"   (dependência do Microsoft.Data.SqlClient 6.1.6)
TEC.ORM.SqlServer/packages.lock.json:365   "System.Security.Cryptography.Pkcs": { "type": "Transitive", "resolved": "9.0.11" }
build/Tec.Build.props:55                   <WarningsAsErrors>$(WarningsAsErrors);NU1901;NU1902;NU1903;NU1904</WarningsAsErrors>
```

Saída real: `error NU1903: Aviso como Erro: O pacote 'System.Security.Cryptography.Pkcs' 9.0.11 tem uma alta vulnerabilidade`.

**Impacto.** Nenhum PR passa no CI (o agendamento semanal também falha) e a versão `0.1.0` já publicada leva a dependência
vulnerável a todo consumidor no .NET 10. O `net8.0` (SqlClient 5.1.9) não é afetado.

**Correção.** Fixar a versão corrigida pela fixação transitiva, que já está ligada (`CentralPackageTransitivePinningEnabled`),
e regenerar os lock files:

```xml
<!-- Directory.Packages.props: transitivo do Microsoft.Data.SqlClient (EF Core 10) com GHSA-846f-f764-76mx -->
<ItemGroup Condition="'$(TargetFramework)' != 'net8.0'">
  <PackageVersion Include="System.Security.Cryptography.Pkcs" Version="<versão corrigida indicada no advisory>" />
</ItemGroup>
```

Alternativa: subir o EF Core 10 / SqlClient para a versão que já referencia o pacote corrigido. Publicar uma `0.1.1`.

### Média

<a id="seg-01"></a>
#### SEG-01 · Valor de coluna vaza para o log 3005 (Média)

**Problema.** Exceções que não são de banco caem em `OrmLog.UnexpectedException`, que registra a exceção inteira (mensagem
e pilha). Nas leituras complexas, a conversão final é feita com `Convert.ChangeType`; no .NET 8+ a `FormatException` traz o
texto de entrada (`The input string '123.456.789-00' was not in a correct format.`). O desserializador do Dapper, por sua vez,
embrulha falhas de conversão numa `DataException` com `Error parsing column N (Nome=valor - tipo)`.

**Evidência.**

```csharp
// OrmQueryExecutor.cs:174
var other => (T)Convert.ChangeType(other, target, CultureInfo.InvariantCulture)

// Diagnostics/OrmOperationRunner.cs:225 e 240: só exceções de banco ficam sem mensagem
if (OrmExceptionTranslator.IsDatabaseException(exception)) { ... return; }
OrmLog.UnexpectedException(logger, exception, operation.Provider, ...);   // exceção completa, em Error
```

**Impacto.** `QueryAsync<long>` sobre uma coluna `varchar` com CPF, ou um DTO com tipo trocado, grava dados pessoais do banco
no log de erro (que costuma ir para terceiros), contrariando a garantia de `OrmLog` e de [🛡️ Segurança](seguranca.md) de nunca
registrar valores de colunas.

**Correção.** Não registrar a mensagem de exceções de conversão: logar só o tipo e a pilha, ou embrulhar numa exceção
sanitizada.

```csharp
// OrmOperationRunner.LogException
var logged = exception is FormatException or InvalidCastException or OverflowException or System.Data.DataException
    ? new OrmRedactedException(exception.GetType().Name, exception.StackTrace)   // sem Message nem InnerException
    : exception;
OrmLog.UnexpectedException(logger, logged, ...);
```

Acrescentar um teste em `Security/Adversarial/LeakageTests.cs` com uma coluna texto lida como `int`.

<a id="seg-02"></a>
#### SEG-02 · Exclusão física sem autoria nem quantidade (Média)

**Problema.** `HardDeleteAsync` usa `ExecuteDeleteAsync`: não passa pelo `AuditInterceptor` (nenhum `DeletedBy`) e o único
rastro é o log 3007, cujo modelo não tem o usuário. No lote (`hard-delete-many`) o identificador é `-` e o número de linhas
removidas não é registrado.

**Evidência.**

```csharp
// OrmRepository.cs:194-201
runner.ExecuteAsync(HardDelete("hard-delete-many"), async Task<Result<long>> (token) => { ...
    long rows = await IncludingSoftDeleted().Where(specification.Criteria).ExecuteDeleteAsync(token)...
// Internal/OrmLog.cs:42-44
"Auditoria ORM {Provider}: EXCLUSÃO FÍSICA - {Operation} de {Target} ({Identifier}) removeu os registros ..."
```

**Impacto.** Para a operação mais destrutiva do componente, a trilha não responde "quem" nem "quanto". O TEC.Observability
não acrescenta o usuário aos logs, então a autoria depende de correlação manual por trace.

**Correção.** Incluir no 3007 o `ICurrentUser.Id` (ou o seu HMAC, conforme `IdentifierLogMode`) e o número de linhas, e
recusar a exclusão física sem identidade autenticada quando a entidade for `IAuditable`, como já acontece nas gravações:

```csharp
[LoggerMessage(3007, LogLevel.Warning, "Auditoria ORM {Provider}: EXCLUSÃO FÍSICA - {Operation} de {Target} ({Identifier}) " +
    "por {Actor} removeu {Rows} linha(s) de fato do banco (irreversível) em {ElapsedMilliseconds} ms.")]
```

<a id="seg-03"></a>
#### SEG-03 · Ordenação por texto permissiva por padrão (Média)

**Problema.** Sem nenhum `[Sortable]` na entidade, `SortablePropertySelector` aceita qualquer propriedade escalar mapeada que
não seja sombra, exclusão lógica ou token de concorrência.

**Evidência.**

```csharp
// OrmRepository.cs:404-408
var candidates = Context.Model.FindEntityType(typeof(TEntity))?.GetProperties().Where(IsSortCandidate).ToList();
...
if (candidates.Any(IsMarkedSortable))
    candidates = candidates.Where(IsMarkedSortable).ToList();
```

**Impacto.** `?sort=PasswordHash` ou `?sort=TaxId` vindos da API permitem inferir o valor de colunas sensíveis pela ordem dos
registros (oráculo de ordenação), e `CreatedBy`/`UpdatedBy` expõem a ordem de identificadores internos. A documentação recomenda
`[Sortable]`, mas quem esquece fica exposto sem aviso.

**Correção.** Falhar fechado: sem `[Sortable]`, aceitar só a chave e as colunas de auditoria de tempo (`CreatedAt`,
`UpdatedAt`), ou exigir uma opção explícita (`OrmOptions.AllowUnmarkedSortBy`, padrão `false`). Alternativa compatível: um
analisador que avise (`TECORM017`) quando `ListAsync` é chamado com entidade sem `[Sortable]`.

<a id="bug-01"></a>
#### BUG-01 · Conexão do Dapper alistada no TransactionScope ambiente (Média)

**Problema.** A string de conexão do executor não define `Enlist=false`. O SqlClient alista a conexão em
`Transaction.Current` ao abrir. Com o EF Core também com conexão aberta no mesmo `TransactionScope`, são duas conexões (strings
diferentes: a de leitura tem `ApplicationIntent=ReadOnly`) e a transação precisa ser promovida a distribuída. Além disso, o
executor marca toda leitura como repetível, mesmo dentro da transação, ao contrário do repositório (`InTransaction`).

**Evidência.**

```csharp
// Security/OrmConnectionSecurity.cs:193-197: nenhum builder.Enlist
builder.PersistSecurityInfo = false;
builder.ApplicationName = options.ApplicationName;
builder.CommandTimeout = options.CommandTimeoutSeconds;
if (kind == OrmConnectionKind.ReadOnly)
    builder.ApplicationIntent = ApplicationIntent.ReadOnly;

// OrmQueryExecutor.cs:99-102
var operation = new OrmOperation(OrmDiagnostics.DapperProvider, operationName, ...) { IsRetryable = true };
```

**Impacto.** Em Linux (contêiner) a promoção lança `PlatformNotSupportedException`; no Windows, `NotSupportedException` sem
`TransactionManager.ImplicitDistributedTransactions`. A consulta vira `ORM_FALHA` com log 3005. A documentação dizia apenas que
o executor "não participa da transação do EF Core".

**Correção.** Desalistar a conexão de leitura (ela é somente leitura e documentada como fora da transação):

```csharp
if (kind == OrmConnectionKind.ReadOnly)
{
    builder.ApplicationIntent = ApplicationIntent.ReadOnly;
    builder.Enlist = false;   // nunca entra em TransactionScope: sem promoção a transação distribuída
}
```

Com isso a nova tentativa continua segura. Acrescentar teste de integração com `TransactionScope` +
`IOrmRepository` + `IOrmQueryExecutor`.

<a id="bug-02"></a>
#### BUG-02 · Propagação da exclusão lógica carrega tudo na memória (Média)

**Problema.** Para cada principal excluído, o interceptor carrega por completo cada navegação em cascata (`LoadAsync`) e,
recursivamente, a de cada dependente; para achar os dependentes, percorre **todas** as entradas do rastreador para cada
chave estrangeira de cada principal.

**Evidência.**

```csharp
// SoftDelete/SoftDeleteInterceptor.cs:103-110
if (softDelete && cascade && dependentIsSoftDelete && !foreignKey.IsOwnership && ... { IsLoaded: false } toLoad)
    await toLoad.LoadAsync(cancellationToken)...
// SoftDelete/SoftDeleteInterceptor.cs:161
foreach (var entry in context.ChangeTracker.Entries()) { ... }
```

**Impacto.** Excluir um cliente com 50.000 pedidos de 10 itens: 1 consulta para os pedidos + 50.000 consultas para os itens,
550.000 entidades rastreadas, varredura quadrática do rastreador e um `UPDATE` por linha num único `SaveChanges`. A requisição
estoura o tempo ou a memória, e um usuário que pode excluir um principal grande consegue degradar o serviço.

**Correção.** Propagar no banco, por nível, com `ExecuteUpdateAsync` dentro da mesma transação do `SaveChanges`:

```csharp
// Por chave estrangeira em cascata, depois de gravar o principal
await context.Set<Order>().Where(o => o.CustomerId == customerId && !o.IsDeleted)
    .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true).SetProperty(o => o.DeletedAt, now), ct);
```

Se a propagação em memória for mantida, impor um teto (`OrmOptions.MaxSoftDeletePropagation`) com falha clara, indexar as
entradas do rastreador por tipo uma vez por salvamento e documentar o custo em [🗑️ Exclusão lógica](exclusao-logica.md).

### Baixa

<a id="seg-04"></a>
#### SEG-04 · Vários comandos de leitura sem `;` contornam o MaxQueryRows (Baixa)

**Problema.** O guarda só recusa "mais de um comando" quando há `;`. `SELECT TOP 1 x FROM a SELECT * FROM b` passa (não tem
palavra proibida). O executor lê só o primeiro resultado, marca a leitura como completa e não cancela o comando; ao fechar,
o `SqlDataReader` lê e descarta todos os resultados restantes.

**Evidência.**

```csharp
// Internal/ReadOnlySqlGuard.cs:266-268
int semicolon = code.IndexOf(';', StringComparison.Ordinal);
if (semicolon >= 0 && ...) return "mais de um comando na mesma consulta.";
// OrmQueryExecutor.cs:137-138: só cancela se o leitor não chegou ao fim do PRIMEIRO resultado
if (!complete) CancelCommand(reader);
```

**Impacto.** O SQL é escrito pelo desenvolvedor, então o risco é de defesa em profundidade: um segundo resultado enorme
trafega inteiro (rede e CPU) apesar do `MaxQueryRows`. Dicas de bloqueio (`WITH (TABLOCKX)`) também são aceitas numa consulta
"somente leitura".

**Correção.** Depois de ler o primeiro resultado, conferir `await reader.NextResultAsync()`: se houver outro, cancelar e
devolver `ORM_CONSULTA_NAO_PERMITIDA`; opcionalmente recusar `UPDLOCK`, `XLOCK`, `TABLOCKX` e `HOLDLOCK` no guarda.

<a id="seg-05"></a>
#### SEG-05 · Listas em `IN` fogem do limite de parâmetros; `LIKE` sem escape (Baixa)

**Problema.** `SqlQuery` conta cada valor como um parâmetro, mas um `IEnumerable` é expandido pelo Dapper em N parâmetros
(`IN @p0` vira `IN (@p01, @p02, ...)`). Também não há utilitário para escapar `%`, `_` e `[` em padrões de `LIKE`.

**Evidência.** `Queries/SqlQuery.cs:65` e `:107` (limite de 2.000 sobre `arguments.Length`/`parameters.Count`);
`OrmQueryExecutor.cs:119-121` (`parameters.Add(name, value)` com a lista inteira).

**Impacto.** Lista com mais de 2.100 itens vira `ORM_FALHA` (erro 8003 do SQL Server), e listas de tamanhos variados poluem o
cache de planos. Com `LIKE {texto}`, um `%` vindo do usuário vira curinga (resultado inesperado e varredura completa). Não é
injeção de SQL: os valores continuam parametrizados.

**Correção.** Contar os itens de `IEnumerable` (exceto `string` e `byte[]`) no limite, documentar que a lista vai sem
parênteses (`IN {ids}`) e oferecer `SqlQuery.EscapeLike(string)` + `ESCAPE '\'`:

```csharp
public static string EscapeLike(string value) =>
    value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");
// $"... WHERE Name LIKE {SqlQuery.EscapeLike(prefix) + "%"} ESCAPE '\'"
```

<a id="seg-06"></a>
#### SEG-06 · Identificador em claro por padrão (Baixa)

**Problema.** `IdentifierLogMode` padrão é `Plain` (`Configuration/OrmOptions.cs:93`) para qualquer tipo de chave.

**Impacto.** Entidade com chave natural `string` (e-mail, CPF) registra o dado pessoal em todo log de escrita (3001) e de
leitura em Debug, até alguém lembrar de configurar `Hashed`.

**Correção.** Modo `Auto` como padrão: `Plain` para `int`, `long` e `Guid`; `Hashed` para `string` e demais tipos.

<a id="bug-03"></a>
#### BUG-03 · Corrida entre `UpdateAsync` e exclusão lógica (Baixa)

**Problema.** A existência é verificada com o filtro global (`AnyAsync`), mas o `UPDATE` gerado filtra só pela chave (e pelos
tokens de concorrência, se houver).

**Evidência.** `OrmRepository.cs:103` (`AnyAsync(HasId(...))`) e `:117` (`SaveAsync`): sem `IsDeleted = 0` no `WHERE` do
`UPDATE`.

**Impacto.** Sem `rowversion`, uma exclusão concorrente entre as duas instruções não impede a atualização: o registro excluído
recebe os novos dados e o chamador recebe sucesso, contrariando "nenhuma operação enxerga registros excluídos".

**Correção.** Recomendar (ou exigir) `rowversion` nas entidades; ou gravar com `ExecuteUpdate` condicionado a
`!IsDeleted`, ou conferir as linhas afetadas.

<a id="bug-04"></a>
#### BUG-04 · `UpdateAsync` devolve autoria enviada pelo chamador (Baixa)

**Problema.** `Keep` tira os campos de criação/exclusão do `UPDATE` igualando `CurrentValue` a `OriginalValue`; numa entidade
anexada como `Modified`, o "original" é o próprio valor recebido. O banco fica correto, mas o objeto devolvido não.

**Evidência.** `Auditing/AuditInterceptor.cs:121-129` (`value.CurrentValue = value.OriginalValue`) e `OrmRepository.cs:118`
(`return entry.Entity`). Para a exclusão lógica há proteção explícita (`ProtectSoftDeleteColumns`); para a autoria, não.

**Impacto.** Um cliente que envia `CreatedBy = "admin"` recebe de volta `"admin"` na resposta do PUT, embora o banco mantenha
o valor real; respostas e caches ficam inconsistentes.

**Correção.** Depois do salvamento, recarregar os campos de autoria (`entry.ReloadAsync` só dessas propriedades) ou
documentar que o retorno não reflete a autoria.

<a id="bug-05"></a>
#### BUG-05 · Dependentes do grafo podem nascer excluídos no `CreateAsync` (Baixa)

**Problema.** `Set.Add` inclui todo o grafo alcançável, mas só a raiz tem `IsDeleted`/`DeletedAt` zerados
(`OrmRepository.cs:57-60`).

**Impacto.** Com entidades geradas por scaffolding (setters públicos) e o DTO desserializado direto na entidade, um item da
coleção pode ser inserido já excluído logicamente. O `AuditInterceptor` cobre a autoria de todas as entradas, mas não a
exclusão lógica.

**Correção.** Zerar os dois campos em toda entrada `Added` que seja `ISoftDelete` (no `SoftDeleteInterceptor`, por exemplo).

<a id="bug-06"></a>
#### BUG-06 · Paginação profunda sem teto (Baixa)

**Problema.** `PageRequest.Validate` só impede o estouro de `int`; cada página executa `COUNT` e `OFFSET` sobre o filtro.

**Evidência.** `Paging/PageRequest.cs:30` (`Page - 1 > (int.MaxValue - PageSize) / PageSize`) e `OrmRepository.cs:280-288`.

**Impacto.** Em tabela com milhões de linhas, `?page=99000&pageSize=100` força o SQL Server a ordenar e pular 9,9 milhões de
linhas a cada chamada (limitado só pelo `CommandTimeout`).

**Correção.** `OrmOptions.MaxOffset` (ex.: 10.000) com `ORM_ENTRADA_INVALIDA` acima dele, e paginação por chave (*keyset*)
como alternativa para listas longas ([✨ Melhorias](#-melhorias-sugeridas)).

<a id="imp-01"></a>
#### IMP-01 · `TECORM014` não vê *method group* (Baixa)

**Problema.** O analisador registra só `OperationKind.Invocation` (`HardDeleteUsageAnalyzer.cs:241-251`).

**Impacto.** `Func<long, CancellationToken, Task<Result>> apagar = repository.HardDeleteAsync;` não gera aviso, e a
chamada posterior pelo delegate também não.

**Correção.** Registrar também `OperationKind.MethodReference` com a mesma regra, e um teste em `HardDeleteAnalyzerTests`.

<a id="imp-02"></a>
#### IMP-02 · Índice de `HasSoftDelete` pouco útil (Baixa)

**Problema.** `builder.HasIndex(e => e.IsDeleted).HasFilter("[IsDeleted] = 0")`
(`SoftDelete/SoftDeleteQueryFilterConvention.cs:207`) indexa um `bit` com um único valor dentro do filtro, e o nome da coluna
está fixo no texto.

**Impacto.** O otimizador praticamente nunca usa esse índice (custo de escrita sem ganho de leitura); com
`HasColumnName("Excluido")` o filtro aponta para uma coluna inexistente e a migration falha.

**Correção.** Deixar `index = false` como padrão e documentar o padrão útil: índices **das colunas de busca** filtrados por
`IsDeleted = 0`, com o nome da coluna obtido do modelo (`GetColumnName`).

<a id="doc-01"></a>
#### DOC-01 · Linhas de tabela soltas no topo de três documentos (Baixa)

**Problema.** `opcoes.md`, `observabilidade.md` e `resiliencia.md` começavam com linhas de tabela (do circuit breaker) antes do
breadcrumb: o cabeçalho quebrava e as linhas não apareciam nas tabelas certas. Corrigido nesta revisão
([📝 Ajustes](#-ajustes-na-documentação)).

### Info

<a id="imp-03"></a>
#### IMP-03 · Lista branca de ordenação recalculada a cada chamada (Info)

`SortablePropertySelector` (`OrmRepository.cs:404-408`) refaz `GetProperties().Where(...).ToList()` e `Attribute.IsDefined`
por listagem. Guardar por `(modelo, TEntity)` um `FrozenDictionary<string, LambdaExpression>` sem diferenciar maiúsculas.

<a id="imp-04"></a>
#### IMP-04 · `string` sempre como `nvarchar` no Dapper (Info)

`parameters.Add(name, value)` (`OrmQueryExecutor.cs:121`) usa o mapeamento padrão do Dapper (`DbType.String`). Contra colunas
`varchar`, o SQL Server converte a coluna (`CONVERT_IMPLICIT`) e perde o *seek* do índice. Aceitar `DbString`/`AnsiString` em
`SqlQuery` e documentar.

<a id="org-02"></a>
#### ORG-02 · Testes faltando (Info)

Não há teste para: `TransactionScope` com repositório + executor (BUG-01); exclusão de principal com muitos dependentes
(BUG-02); valor de coluna no log 3005 (SEG-01); autoria e contagem no 3007 (SEG-02); `SELECT ... SELECT ...` sem `;` e segundo
resultado grande (SEG-04); *method group* no `TECORM014` (IMP-01). A suíte atual (316/314 unitários) passa.

<a id="doc-02"></a>
#### DOC-02 · Divergências menores entre documentação e código (Info)

Os modelos dos eventos 3200/3201 em `observabilidade.md` não tinham o prefixo `ORM:` do código e as categorias de log não
citavam o interceptor nem a limpeza da idempotência; `consultas-sql.md` não mencionava o `TransactionScope` (BUG-01) nem o
caso `SELECT 1 SELECT 2` (SEG-04). Corrigido nesta revisão.

---

## ✨ Melhorias sugeridas

Em ordem de prioridade:

| # | Melhoria | Por quê |
|:-:|---|---|
| 1 | **Fixar `System.Security.Cryptography.Pkcs`** e publicar `0.1.1` | Destrava o CI e tira a vulnerabilidade dos consumidores ([ORG-01](#org-01)) |
| 2 | **Log 3005 sem mensagem de exceções de conversão** + teste de vazamento | Fecha o único caminho encontrado de valor de coluna em log ([SEG-01](#seg-01)) |
| 3 | **`Enlist=false` na conexão de leitura** | Executor seguro dentro de `TransactionScope` ([BUG-01](#bug-01)) |
| 4 | **Autoria na exclusão física**: ator e linhas no 3007, identidade obrigatória para `IAuditable` | Trilha completa da operação irreversível ([SEG-02](#seg-02)) |
| 5 | **Propagação da exclusão lógica no banco** (`ExecuteUpdate` por nível) ou com teto | Exclusão de agregados grandes sem N+1 nem estouro de memória ([BUG-02](#bug-02)) |
| 6 | **`SortBy` falha fechada** (`AllowUnmarkedSortBy = false`) ou analisador `TECORM017` | Segurança por padrão na ordenação vinda da API ([SEG-03](#seg-03)) |
| 7 | **Paginação por chave** (`ListAfterAsync(cursor, pageSize, spec)`) e `MaxOffset` | Listas longas com custo constante, sem `COUNT` obrigatório ([BUG-06](#bug-06)) |
| 8 | **`IdentifierLogMode.Auto`** como padrão | Chaves naturais pessoais nunca em claro sem configuração ([SEG-06](#seg-06)) |
| 9 | **`SqlQuery.EscapeLike`**, contagem de itens de lista no limite e suporte a `DbString` | `LIKE` previsível, `IN` grande com erro claro, índices `varchar` usados ([SEG-05](#seg-05), [IMP-04](#imp-04)) |
| 10 | **Guarda de somente leitura com `NextResult`** e recusa de dicas de bloqueio | Fecha o contorno do `MaxQueryRows` ([SEG-04](#seg-04)) |
| 11 | **Restauração da exclusão lógica** (`RestoreAsync(id)`, com autoria) | Hoje só há exclusão; reverter exige SQL próprio com `IgnoreQueryFilters` |
| 12 | **`TECORM014` para *method group*** e alerta para `IgnoreQueryFilters()` fora do componente | Alerta onde os excluídos podem vazar ([IMP-01](#imp-01)) |

---

## 📝 Ajustes na documentação

Feitos nesta revisão:

| Arquivo | Ajuste |
|---|---|
| [`opcoes.md`](opcoes.md) | Removidas as duas linhas soltas antes do breadcrumb; `CircuitBreaker` entrou na tabela de opções e os erros de validação na tabela de erros, separando `TecOrm:CircuitBreaker` (nulo) de `TecOrm:CircuitBreaker:<Opção>` (como em `OrmOptions.Validate`) |
| [`observabilidade.md`](observabilidade.md) | Removidas as seis linhas soltas do topo; `CircuitStateChangesName` foi para a tabela de constantes, a métrica `orm.circuit.state_changes` para a de sinais e os eventos 3105–3108 para a de logs; 3200/3201 com o texto real (`ORM: ...`, 3201 com a exceção); categorias de log completadas |
| [`resiliencia.md`](resiliencia.md) | As cinco linhas de `CircuitBreaker.*` saíram do topo e foram para a tabela de opções |
| [`consultas-sql.md`](consultas-sql.md) | Linha "Transação" cita o alistamento em `TransactionScope`; tabela da verificação ganhou o caso `SELECT 1 SELECT 2` |
| [`README.md`](README.md) | Tema 18 (esta revisão) na tabela e no mapa |
| [`desenvolvimento.md`](desenvolvimento.md) | Rodapé com link para esta revisão |

Feitos junto com as correções (2026-10-19):

- [🗑️ Exclusão lógica](exclusao-logica.md): custo da propagação (BUG-02) e o padrão de índice útil (IMP-02).
- [🔎 Especificações e paginação](especificacoes-e-paginacao.md): comportamento padrão do `SortBy` (SEG-03) e teto de página
  (BUG-06).
- [📊 Consultas SQL](consultas-sql.md): `IN {lista}` sem parênteses, limite efetivo de parâmetros e escape de `LIKE` (SEG-05).
- [📈 Observabilidade](observabilidade.md) e [🕵️ Auditoria](auditoria.md): autoria da exclusão física (SEG-02).
- [📝 Changelog](../CHANGELOG.md): correções registradas na `0.2.0` (MINOR: há mudanças de comportamento); também [⚙️ Opções](opcoes.md), [📝 Repositório](repositorio-crud.md), [🛡️ Segurança](seguranca.md), [🔁 Idempotência](idempotencia.md) e [🩺 Diagnósticos](diagnosticos-do-gerador.md).

---
⬅️ [💻 Desenvolvimento local](desenvolvimento.md) · [📚 Índice](README.md)
