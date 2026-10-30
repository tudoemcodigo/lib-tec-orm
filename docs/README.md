<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

[🏠 TEC.ORM](../README.md) › 📚 Documentação

# 📚 Documentação do TEC.ORM

> Referência completa dos pacotes da família **TEC.ORM**:
> - `TEC.ORM`: abstrações, DTOs e gerador de mapeamento;
> - `TEC.ORM.EntityFrameworkCore`: motor base agnóstico, auditoria histórica com diffs, idempotência e resiliência;
> - `TEC.ORM.SqlServer`, `TEC.ORM.PostgreSql`, `TEC.ORM.MySql` e `TEC.ORM.Oracle`: provedores especializados para cada banco de dados.

## 📑 Sumário

- [🗂️ Temas](#️-temas)
- [🗺️ Mapa dos temas](#️-mapa-dos-temas)
- [📐 Convenções desta documentação](#-convenções-desta-documentação)

---

## 🗂️ Temas

| # | Tema | O que responde |
|:-:|---|---|
| 1 | [📝 Repositório genérico (CRUD)](repositorio-crud.md) | `IOrmRepository<TEntity, TKey>`, `OrmRepositoryExtensions` e `OrmRepository<TEntity, TKey>`: criar, ler, atualizar, excluir (lógica e física), buscar, listar, projetar para DTO e especializar |
| 2 | [🔎 Especificações e paginação](especificacoes-e-paginacao.md) | `Specification<T>`, `PageRequest`, `[Sortable]` e como a listagem monta a consulta |
| 3 | [📊 Consultas SQL](consultas-sql.md) | `IOrmQueryExecutor`, `SqlQuery`, `MaxQueryRows`, join com `splitOn` e verificação de somente leitura |
| 4 | [🗑️ Exclusão lógica](exclusao-logica.md) | `ISoftDelete`, `Entity<TKey>`, `OrmDbContext`, convenções, filtro global nomeado e propagação |
| 5 | [🕵️ Auditoria](auditoria.md) | `IAuditable`, `AuditableEntity<TKey>`, `OrmIdentity`, `UseTecOrmIdentity` e identidade em cada cenário |
| 6 | [🔁 Transação](transacao.md) | `OrmUnitOfWork` (`IUnitOfWork` do TEC.Cqrs), commands aninhados e uso manual |
| 7 | [🔂 Idempotência](idempotencia.md) | `EfIdempotencyStore<TContext>`, `AddTecIdempotency`, `AddTecOrmIdempotency` e limpeza: `Idempotency-Key` com reserva atômica no banco |
| 8 | [🔄 Mapeamento entidade ↔ DTO](mapeamento.md) | `[MapFrom]` e demais atributos, conversões, aninhados, coleções, `MaxDepth`, conversores, `ApplyTo`/`ToEntity` |
| 9 | [🛑 Diagnósticos do gerador](diagnosticos-do-gerador.md) | Todos os `TECORM001`–`TECORM016`: mensagem, exemplo e correção |
| 10 | [🗄️ SQL Server](sqlserver.md) | `AddTecOrm`, `UseTecOrm`, conexão do cofre e política, health check, vários contextos e extensão |
| 11 | [⚙️ Opções](opcoes.md) | `OrmOptions`, `IdentifierLogMode`, limites e `appsettings.json` |
| 12 | [🔁 Resiliência](resiliencia.md) | Novas tentativas só em leituras, falhas transitórias (Azure SQL), pool esgotado e limites |
| 13 | [📈 Observabilidade](observabilidade.md) | `IOrmOperationRunner`, `OrmOperation`, `OrmDiagnostics`, traces, métricas e eventos de log |
| 14 | [❌ Erros](erros.md) | `OrmErrors`: códigos, `ErrorType`, HTTP e tradução das exceções do banco |
| 15 | [🛡️ Segurança](seguranca.md) | Ameaças e controles, o que nunca é registrado, responsabilidades e checklist de produção |
| 16 | [🧪 Testes](testes.md) | Categorias, como rodar local, SQL Server descartável, variáveis `TEC_TESTES_*`/`TEC_CARGA_*` e suítes |
| 17 | [💻 Desenvolvimento local](desenvolvimento.md) | Compilar (feed `tec-interno` por padrão, repositórios vizinhos sob demanda), empacotar e regenerar lock files |
| 18 | [🔍 Revisão técnica](revisao-tecnica.md) | Revisão de segurança e qualidade de 2026-10-19: achados por severidade, evidências, correções sugeridas e melhorias |

Fora de `docs/`: [⚙️ CI/CD](../.github/workflows/README.md) · [📝 Changelog](../CHANGELOG.md) · READMEs dos pacotes
([TEC.ORM](../TEC.ORM/README.md), [TEC.ORM.EntityFrameworkCore](../TEC.ORM.EntityFrameworkCore/README.md),
[TEC.ORM.SqlServer](../TEC.ORM.SqlServer/README.md), [TEC.ORM.PostgreSql](../TEC.ORM.PostgreSql/README.md),
[TEC.ORM.MySql](../TEC.ORM.MySql/README.md), [TEC.ORM.Oracle](../TEC.ORM.Oracle/README.md)).

Os quatro provedores usam o mesmo pipeline (`TEC.ORM.EntityFrameworkCore`), e os guias usam o SQL Server (`AddTecOrm`) nos
exemplos. O que muda por banco (política da conexão, validador de leitura, transação somente leitura, tradução de erros e SQL
da idempotência) está no README de cada provedor e nas seções de diferenças em [Consultas SQL](consultas-sql.md),
[Erros](erros.md), [Idempotência](idempotencia.md) e [Segurança](seguranca.md).

---

## 🗺️ Mapa dos temas

```mermaid
flowchart TD
    A["Primeira vez?"] --> S["🗄️ sqlserver.md<br/>AddTecOrm e conexão do cofre"]
    S --> E["🗑️ exclusao-logica.md<br/>entidades e contexto"]
    E --> Q{"O que a aplicação precisa?"}
    Q -->|"CRUD"| R["📝 repositorio-crud.md"]
    Q -->|"listas e filtros"| P["🔎 especificacoes-e-paginacao.md"]
    Q -->|"relatório, agregação, join"| C["📊 consultas-sql.md"]
    Q -->|"DTO de API"| M["🔄 mapeamento.md"]
    Q -->|"quem alterou"| AU["🕵️ auditoria.md"]
    R --> T["🔁 transacao.md"]
    M -.->|"build falhou"| D["🛑 diagnosticos-do-gerador.md"]
    R --> O["⚙️ opcoes.md · 🔁 resiliencia.md"]
    C --> O
    O --> OB["📈 observabilidade.md · ❌ erros.md"]
    OB --> SEC["🛡️ seguranca.md<br/>checklist de produção"]
    SEC -.-> TS["🧪 testes.md · 💻 desenvolvimento.md"]
    SEC -.-> RV["🔍 revisao-tecnica.md<br/>achados e melhorias"]
```

---

## 📐 Convenções desta documentação

| Convenção | Significado |
|---|---|
| Fonte da verdade | O código atual de `TEC.ORM`, `TEC.ORM.EntityFrameworkCore`, `TEC.ORM.Mapping.Generator` e dos provedores (`TEC.ORM.SqlServer`, `TEC.ORM.PostgreSql`, `TEC.ORM.MySql` e `TEC.ORM.Oracle`) |
| Estrutura | Breadcrumb, 📑 Sumário, 🎯 Visão geral, 🚀 Uso, ⚙️ Opções, ❌ Erros, 🛡️ Segurança, ❓ Perguntas frequentes e rodapé de navegação |
| Exemplos | Domínio fictício de vendas (`Customer`, `Order`, `Product`, `Account`, `SalesContext`): tipos da aplicação, não da biblioteca. Identificadores em inglês; comentários e mensagens em português |
| `Result`, `Error`, `ErrorType`, `PagedResult`, `ICurrentUser` | Vêm do [TEC.Core](https://github.com/tudoemcodigo/lib-tec-core) |
| Status HTTP por `ErrorType` | `Validation` 400 · `Unauthorized` 401 · `NotFound` 404 · `Conflict` 409 · `Failure` 500 · `ExternalService` 502 (🔒 mensagem oculta do cliente) |
| Configuração inválida | Falha na **inicialização** (`AddTecOrm`), não na primeira requisição |
| Placeholders | Valores entre `< >` (`<nome-do-cofre>`, `<usuario>`, `<PAT>`) são do seu ambiente |
| Alertas | `[!NOTE]` comportamento · `[!TIP]` boa prática · `[!IMPORTANT]` requisito · `[!WARNING]` armadilha · `[!CAUTION]` risco de segurança ou perda de dados |

---
[🏠 README](../README.md) · [📝 Repositório genérico](repositorio-crud.md) ➡️
