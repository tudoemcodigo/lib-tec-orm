[🏠 TEC.ORM](../README.md) › [📚 Documentação](README.md) › Idempotência

# 🔂 Idempotência

> Guarda no banco da aplicação as respostas das requisições com `Idempotency-Key`, com reserva atômica: várias instâncias da API nunca executam a mesma operação duas vezes.

## 📑 Sumário

- [🎯 Visão geral](#-visão-geral)
- [🚀 Uso](#-uso)
  - [Mapear a tabela](#mapear-a-tabela)
  - [Registrar](#registrar)
  - [Limpeza](#limpeza)
- [⚙️ Opções](#️-opções)
- [❌ Erros](#-erros)
- [🛡️ Segurança](#️-segurança)
- [❓ Perguntas frequentes](#-perguntas-frequentes)

---

## 🎯 Visão geral

O `EfIdempotencyStore<TContext>` implementa o `IIdempotencyStore` do **TEC.Cqrs**, usado pelo middleware `UseTecIdempotency` do
`TEC.Cqrs.AspNetCore` (ver a [documentação da idempotência HTTP](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/docs/idempotencia.md)).

| Tipo | Namespace | Para que serve |
|---|---|---|
| `modelBuilder.AddTecIdempotency(schema, tableName)` | `TEC.ORM.SqlServer.Idempotency` | Mapeia a tabela no modelo do contexto |
| `services.AddTecOrmIdempotency<TContext>(...)` | `TEC.ORM.SqlServer.DependencyInjection` | Registra o store (Scoped) e a limpeza |
| `EfIdempotencyStore<TContext>` | `TEC.ORM.SqlServer.Idempotency` | O store (`TryBeginAsync`, `CompleteAsync`, `AbandonAsync`, `PurgeExpiredAsync`) |
| `OrmIdempotencyOptions` | `TEC.ORM.SqlServer.Idempotency` | Opções da limpeza |

```mermaid
flowchart LR
    R(["TryBeginAsync"]) --> I["INSERT ... WHERE NOT EXISTS<br/>(UPDLOCK, HOLDLOCK)"]
    I -->|"1 linha"| S["✅ Started (LockId)"]
    I -->|"já existe"| L["Lê a linha"]
    L -->|"concluída e válida"| C["Completed / Mismatch"]
    L -->|"reservada e no prazo"| P["InProgress / Mismatch"]
    L -->|"expirada"| U["UPDATE ... WHERE LockId = anterior"]
    U -->|"1 linha"| S
    U -->|"0 linhas"| P
```

| Coluna | Tipo | Conteúdo |
|---|---|---|
| `Scope`, `IdempotencyKey` | `nvarchar(200) COLLATE Latin1_General_100_BIN2` (chave primária) | Escopo (no TEC.Cqrs 0.2.0, hash SHA-256 hex de 64 caracteres) e chave do cliente, comparados de forma **ordinal** (`Abc` ≠ `abc`), como exige o contrato do `IIdempotencyStore` |
| `RequestHash` | `varchar(128)` | SHA-256 da requisição |
| `LockId`, `State`, `LockedUntil` | — | Reserva (0 = em andamento, 1 = concluída) |
| `RetentionSeconds`, `ExpiresAt` | — | Retenção e expiração (índice em `ExpiresAt`) |
| `StatusCode`, `ContentType`, `HeadersJson`, `Body` | — | Resposta guardada |

---

## 🚀 Uso

### Mapear a tabela

```csharp
using TEC.ORM.SqlServer.Idempotency;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddTecIdempotency(schema: "app");   // tabela app.IdempotencyKeys
    }
}
```

Depois gere e aplique a migration (`dotnet ef migrations add Idempotencia`).

> [!IMPORTANT]
> **Tabela criada antes da 0.2.0** (collation padrão do banco, em geral sem diferenciar maiúsculas): a próxima migration
> gerada traz um `AlterColumn` com a collation binária, mas o SQL Server não altera a collation de coluna que está na chave
> primária. Edite a migration para usar o script abaixo (a tabela só guarda respostas temporárias: se preferir, esvazie-a
> antes). Chaves que só diferem em maiúsculas passam a ser distintas.
>
> ```sql
> ALTER TABLE app.IdempotencyKeys DROP CONSTRAINT PK_IdempotencyKeys;
> ALTER TABLE app.IdempotencyKeys ALTER COLUMN Scope nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL;
> ALTER TABLE app.IdempotencyKeys ALTER COLUMN IdempotencyKey nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL;
> ALTER TABLE app.IdempotencyKeys ADD CONSTRAINT PK_IdempotencyKeys PRIMARY KEY (Scope, IdempotencyKey);
> ```

### Registrar

```csharp
builder.Services.AddTecOrm<AppDbContext>(o => o.ConnectionSecretName = "sql-app");
builder.Services.AddTecOrmIdempotency<AppDbContext>();

builder.Services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<Program>())
    .AddAspNetCore()
    .AddIdempotency();

app.UseAuthentication();
app.UseAuthorization();
app.UseTecIdempotency();
app.MapPost("/pedidos", ...).WithIdempotency();
```

### Limpeza

Um `BackgroundService` remove, a cada `CleanupInterval`, as respostas fora da retenção e as reservas vencidas, em lotes de
`CleanupBatchSize` (até `MaxBatchesPerCleanup` por ciclo). Para limpar por um job próprio, desligue com `EnableCleanup = false` e chame
`EfIdempotencyStore<TContext>.PurgeExpiredAsync(batchSize, ct)`.

---

## ⚙️ Opções

| Opção | Padrão | Descrição |
|---|---|---|
| `EnableCleanup` | `true` | Limpeza periódica em segundo plano |
| `CleanupInterval` | 1 h | Intervalo entre limpezas (1 min a 1 dia) |
| `CleanupBatchSize` | 1.000 | Linhas por comando (1 a 10.000) |
| `MaxBatchesPerCleanup` | 50 | Lotes por ciclo (1 a 1.000) |

Retenção, prazo da reserva e limites de tamanho são do `IdempotencyOptions` do TEC.Cqrs.AspNetCore.

## ❌ Erros

| Exceção | Quando ocorre | O que fazer |
|---|---|---|
| `InvalidOperationException` "A tabela de idempotência não está mapeada" | Contexto sem `AddTecIdempotency()` | Mapeie a tabela e gere a migration |
| `InvalidOperationException` "Já existe um IIdempotencyStore registrado" | Dois stores (ex.: também `AddInMemoryIdempotencyStore`) | Registre um só |
| `ArgumentOutOfRangeException` | Opção fora dos limites | Corrija a opção |
| Log 3201 (Warning) | Falha na limpeza (banco indisponível) | Nada: tenta de novo no próximo ciclo |

## 🛡️ Segurança

> [!WARNING]
> A tabela guarda o **corpo das respostas** por até `Retention` (padrão 24 h). Se as respostas tiverem dados pessoais, trate a tabela
> como os demais dados da aplicação (backup, acesso, LGPD) e prefira respostas enxutas (ids e `Location`) nos endpoints idempotentes.

- O store nunca chama `SaveChanges`: só comandos diretos e leituras sem rastreamento. Alterações pendentes da aplicação no mesmo
  contexto não são gravadas por ele.
- Identificadores do SQL vêm do modelo (escapados); todos os valores são parâmetros.

## ❓ Perguntas frequentes

<details>
<summary><b>Posso usar um banco ou contexto separado?</b></summary>

Sim: mapeie a tabela em outro contexto registrado com `AddTecOrm<OutroContext>` e use `AddTecOrmIdempotency<OutroContext>()`.
</details>

<details>
<summary><b>O store participa da transação do command?</b></summary>

Participa da transação corrente do contexto, se houver. O middleware HTTP roda antes e depois do pipeline, fora de transação; por
isso a reserva fica visível às outras instâncias imediatamente.
</details>

---

⬅️ [🔁 Transação](transacao.md) · [📚 Índice](README.md) · [🔄 Mapeamento](mapeamento.md) ➡️
