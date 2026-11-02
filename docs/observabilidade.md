[🏠 TEC.ORM](../README.md) › [📚 Documentação](README.md) › 📈 Observabilidade

# 📈 Observabilidade

> Toda operação do TEC.ORM gera um trace, uma medição de duração e um log de auditoria, em sucesso e em erro, sem nunca
> registrar SQL, valores ou dados da conexão.

## 📑 Sumário

- [🎯 Visão geral](#-visão-geral)
- [🚀 Uso](#-uso)
  - [Com o TEC.Observability](#com-o-tecobservability)
  - [Sem o TEC.Observability](#sem-o-tecobservability)
  - [IOrmOperationRunner e OrmOperation](#iormoperationrunner-e-ormoperation)
  - [OrmDiagnostics, traces e métricas](#ormdiagnostics-traces-e-métricas)
  - [Eventos de log](#eventos-de-log)
- [⚙️ Opções](#️-opções)
- [❌ Erros](#-erros)
- [🛡️ Segurança](#️-segurança)
- [❓ Perguntas frequentes](#-perguntas-frequentes)

---

## 🎯 Visão geral

```mermaid
sequenceDiagram
    autonumber
    participant Repo as OrmRepository / OrmQueryExecutor
    participant Run as OrmOperationRunner
    participant Op as Operação
    participant OT as ActivitySource e Meter TEC.ORM
    participant Log as ILogger
    Repo->>Run: ExecuteAsync(OrmOperation, ação)
    Run->>OT: StartActivity operação + alvo (Client)
    Run->>Op: executa
    alt falha transitória numa leitura repetível
        Op-->>Run: exceção
        Run->>Log: 8008 · espera e repete
    end
    Op-->>Run: Result ou exceção (traduzida para OrmErrors)
    Run->>OT: tec.orm.operation.duration · orm.success · error.type · orm.retries
    Run->>Log: evento 8000–8007
    Run-->>Repo: Result
    Note over Run: Cancelamento: métrica e trace com canceled, log 8006 e relança
```

O TEC.ORM **não depende** do TEC.Observability: emite `ActivitySource` e `Meter` da BCL com o nome `TEC.ORM`, que qualquer
OpenTelemetry pode assinar.

---

## 🚀 Uso

### Com o TEC.Observability

```csharp
builder.Services.AddTecObservability(builder.Configuration)     // já assina as fontes "TEC.*", inclusive "TEC.ORM"
    .HealthChecks.AddTecOrm();                                   // banco no /health/ready
builder.Services.AddTecOrm<SalesContext>(orm => orm.ConnectionSecretName = "sales-sql-rw");
```

### Sem o TEC.Observability

```csharp
using TEC.ORM.EntityFrameworkCore.Diagnostics;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(OrmDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(OrmDiagnostics.MeterName));
```

### IOrmOperationRunner e OrmOperation

> `TEC.ORM.EntityFrameworkCore.Diagnostics` · pacote `TEC.ORM.EntityFrameworkCore` (o mesmo nos quatro provedores)

`IOrmOperationRunner` executa cada operação: cria o `Activity`, mede, registra, traduz exceções em `Result` e repete
leituras em falha transitória. É ponto de extensão (os repositórios dependem só da interface).

| Membro | Retorno | Descrição |
|---|---|---|
| `ExecuteAsync<T>(OrmOperation operation, Func<CancellationToken, Task<Result<T>>> action, CancellationToken ct)` | `Task<Result<T>>` | Operação com valor |
| `ExecuteAsync(OrmOperation operation, Func<CancellationToken, Task<Result>> action, CancellationToken ct)` | `Task<Result>` | Operação sem valor |

Implementação: `OrmOperationRunner(ILogger<OrmOperationRunner> logger, OrmOptions options, TimeProvider? time = null)`
(`time` é o relógio das esperas entre tentativas; `null` usa `TimeProvider.System`).

`OrmOperation(string provider, string operation, string target, bool isWrite)`:

| Membro | Tipo | Descrição |
|---|---|---|
| `Provider` | `string` | `entityframework` ou `dapper` |
| `Operation` | `string` | `create`, `get`, `update`, `delete`, `hard-delete`, `hard-delete-many`, `find`, `list`, `exists`, `count`, `get-dto`, `find-dto`, `list-dto`, `query`, `query-single`, `scalar`, `query-join` |
| `Target` | `string` | Nome da entidade ou `SqlQuery.Name` |
| `IsWrite` | `bool` | Escrita: log de auditoria em `Information` |
| `Identifier` | `object?` | Só no log, conforme `IdentifierLogMode`; pode ser definido durante a operação |
| `IsHardDelete` | `bool` (`init`) | Sucesso no log 8007 (`Warning`) em vez do 8001 |
| `Actor` | `string?` | Autor (id do `ICurrentUser`) no log 8007, conforme `IdentifierLogMode`; `-` sem identidade |
| `AffectedRows` | `long?` | Linhas removidas no log 8007; `-` se não informado |
| `IsRetryable` | `bool` (`init`) | Pode ser repetida em falha transitória (só leitura fora de transação; ignorado em escritas) |

### OrmDiagnostics, traces e métricas

| Constante (`OrmDiagnostics`) | Valor |
|---|---|
| `ActivitySourceName` / `MeterName` | `"TEC.ORM"` |
| `OperationDurationName` | `"tec.orm.operation.duration"` |
| `EntityFrameworkProvider` / `DapperProvider` | `"entityframework"` / `"dapper"` |
| `UnknownDbSystem` | `"other_sql"` (sem dialeto; com dialeto, `db.system.name` é o `ISqlDialect.DbSystemName`: `microsoft.sql_server`, `postgresql`, `mysql` ou `oracle.db`) |
| `CanceledErrorType` | `"canceled"` |
| `CircuitStateChangesName` | `"tec.orm.circuit.state_changes"` |

| Sinal | Nome | Atributos |
|---|---|---|
| Trace | `Activity` `Client` `"{operação} {alvo}"` (ex.: `create Customer`, `query reports.sales`) | `db.system.name`, `orm.provider`, `orm.operation`, `orm.target`, `orm.success`, `orm.retries` (só com nova tentativa) e, em falha, `error.type` + status `Error` |
| Métrica | `tec.orm.operation.duration` (histograma, `s`) | `db.system.name`, `orm.provider`, `orm.operation`, `orm.target` e, em falha, `error.type` (código de `OrmErrors` ou `canceled`) |
| Métrica | `tec.orm.circuit.state_changes` (contador) | `orm.connection` (`escrita`/`leitura`), `orm.circuit.state` (`open`, `half_open`, `closed`) |

A contagem por operação e resultado vem do próprio histograma (ex.: taxa de erro por entidade = contagem com
`error.type` ÷ total, agrupado por `orm.target`).

### Eventos de log

Faixa de EventId do componente: **8000-8999** (tabela única de faixas da Doutrina, CMP-OBS-07). Antes ficavam em 3000-3201; o
sufixo foi mantido (3001 virou 8001). Filtros e alertas por EventId 3xxx devem passar a 8xxx.

Mensagens geradas com `LoggerMessage`. Categorias (as mesmas nos quatro provedores):
`TEC.ORM.EntityFrameworkCore.Diagnostics.OrmOperationRunner`, `TEC.ORM.EntityFrameworkCore.Security.OrmConnectionSecurity`
(inclusive o circuito das conexões abertas por ela), `TEC.ORM.EntityFrameworkCore.Security.SecretConnectionInterceptor`
(circuito da conexão do EF Core), `TEC.ORM.EntityFrameworkCore.HealthChecks.OrmHealthCheck` e
`TEC.ORM.EntityFrameworkCore.Idempotency.IdempotencyCleanupService` (8200–8201).

| Evento | Nível | Mensagem (modelo) |
|---|---|---|
| 8000 | Debug | ORM {Provider}: {Operation} de {Target} ({Identifier}) concluída em {ElapsedMilliseconds} ms. |
| 8001 | Information | Auditoria ORM {Provider}: {Operation} de {Target} ({Identifier}) concluída em {ElapsedMilliseconds} ms. |
| 8002 | Information | ORM {Provider}: {Operation} de {Target} ({Identifier}) retornou {ErrorCode} em {ElapsedMilliseconds} ms. |
| 8003 | Warning | Auditoria ORM {Provider}: {Operation} de {Target} ({Identifier}) retornou {ErrorCode} em {ElapsedMilliseconds} ms. |
| 8004 | Error | ORM {Provider}: {Operation} de {Target} ({Identifier}) falhou por infraestrutura: {ErrorCode} ({ExceptionType}, erro do banco {DatabaseErrorCode}) em {ElapsedMilliseconds} ms. |
| 8005 | Error | ORM {Provider}: {Operation} de {Target} ({Identifier}) lançou {ExceptionType} inesperada; convertida em {ErrorCode}. *(com a exceção; as de conversão de valor, sem a mensagem)* |
| 8006 | Debug | ORM {Provider}: {Operation} de {Target} ({Identifier}) cancelada após {ElapsedMilliseconds} ms. |
| 8007 | Warning | Auditoria ORM {Provider}: EXCLUSÃO FÍSICA - {Operation} de {Target} ({Identifier}) por {Actor} removeu {Rows} linha(s) de fato do banco (irreversível) em {ElapsedMilliseconds} ms. |
| 8008 | Warning | ORM {Provider}: {Operation} de {Target} teve falha transitória {ErrorCode} (erro do banco {DatabaseErrorCode}); nova tentativa {Retry} de {MaxRetries}. |
| 8100 | Error | ORM: segredo da conexão {Kind} indisponível no cofre: {ErrorCode}. |
| 8101 | Error | ORM: segredo da conexão {Kind} recusado pela política de segurança: {Reason}. |
| 8102 | Error | ORM: falha ao abrir a conexão {Kind} ({ExceptionType}, erro do banco {DatabaseErrorCode}). |
| 8103 | Warning | ORM: TrustServerCertificate habilitado na conexão {Kind}: o certificado do servidor não é validado (use só em desenvolvimento). |
| 8104 | Warning | Health check do ORM falhou: {ErrorCode}. |
| 8105 | Warning | ORM: circuito da conexão {Kind} aberto após falhas repetidas ao abrir conexões; aberturas recusadas por {BreakSeconds} s. |
| 8106 | Information | ORM: circuito da conexão {Kind} meio-aberto; testando o banco com uma abertura. |
| 8107 | Information | ORM: circuito da conexão {Kind} fechado; o banco voltou a aceitar conexões. |
| 8108 | Debug | ORM: abertura da conexão {Kind} recusada com o circuito aberto. |
| 8200 | Debug | ORM: limpeza da idempotência removeu {Count} chave(s) expirada(s) de {Context}. |
| 8201 | Warning | ORM: falha na limpeza da idempotência de {Context}; nova tentativa no próximo ciclo. *(com a exceção)* |

`{Kind}` é `escrita` ou `leitura`. Falhas esperadas de leitura saem em 8002; de escrita, em 8003; de infraestrutura
(`ExternalService`), em 8004. `{Identifier}` segue o `IdentifierLogMode` ([⚙️ Opções](opcoes.md#identifierlogmode)).

---

## ⚙️ Opções

| Configuração | Padrão | Descrição |
|---|---|---|
| `OrmOptions.IdentifierLogMode` | `Auto` | Identificador (e autor da exclusão física) no log |
| `Logging:LogLevel:TEC.ORM.EntityFrameworkCore.Diagnostics.OrmOperationRunner` | do host | Mantenha `Information` em produção (auditoria das escritas) |

```json
{ "Logging": { "LogLevel": { "TEC.ORM.EntityFrameworkCore.Diagnostics.OrmOperationRunner": "Information" } } }
```

---

## ❌ Erros

| Código / situação | Onde aparece |
|---|---|
| Qualquer código de `OrmErrors` | `error.type` (métrica e trace) e `{ErrorCode}` (log) |
| `canceled` | `error.type` de operação cancelada (log 8006) |
| Exceção inesperada | `ORM_FALHA` + log 8005 com a pilha (ex.: navegação inexistente no `Include`); falha de conversão de valor (`FormatException`, `InvalidCastException`, `OverflowException`, `DataException` do Dapper) vai sem a mensagem, que traria o valor da coluna |

---

## 🛡️ Segurança

> [!IMPORTANT]
> Traces e métricas costumam ir para terceiros: **nunca** levam identificador, SQL, valores de parâmetros ou dados da
> conexão. O identificador fica só no log de auditoria.

- Exceções de banco não têm a mensagem registrada (pode trazer o valor de uma chave duplicada): só tipo, número do erro e
  código. Os eventos de erro do próprio EF Core que repetiriam essa mensagem ficam desligados pelo `AddTecOrm`.
- Exceções de conversão (ex.: coluna texto lida como número) também não têm a mensagem registrada: o .NET 8+ e o Dapper
  repetem nela o valor lido. Ficam os tipos da cadeia e a pilha.
- Em claro (`Plain`, ou `Auto` com chave técnica), caracteres de controle, de formatação (U+202E) e separadores U+2028/U+2029 do identificador viram `?`:
  sem injeção de linhas nem falsificação visual no log.

---

## ❓ Perguntas frequentes

<details>
<summary>Vale alertar em algum evento?</summary>

Sim: **8007** (exclusão física), **8004** (infraestrutura) e o aumento de **8008** (novas tentativas, sinal de rede ou
failover instável).

</details>

<details>
<summary>Antes era <code>AddEnterpriseObservability</code>; mudou?</summary>

O nome atual é `AddTecObservability`, que assina todas as fontes `TEC.*`.

</details>

---
⬅️ [🔁 Resiliência](resiliencia.md) · [📚 Índice](README.md) · [❌ Erros](erros.md) ➡️
