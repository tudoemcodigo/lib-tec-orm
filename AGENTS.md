# Instruções para agentes — TEC.ORM

Este arquivo complementa as instruções herdadas de `AGENT_BASE.md`. O TEC.ORM fornece persistência padronizada, agnóstica e segura por padrão: CRUD para qualquer entidade, consultas SQL via Dapper, suporte a SQL Server, PostgreSQL, MySQL e Oracle, trilha histórica de auditoria com diffs, exclusão lógica e conversão entidade ↔ DTO gerada na compilação, com credenciais lidas exclusivamente de cofres.

## Escopo e segurança de alterações

- O escopo padrão é este repositório. Não altere outros repositórios `TEC.*`, não publique pacotes/tags/releases e não rode sincronizações que escrevam fora daqui sem pedido explícito.
- Antes de editar, confira `git status` e `git diff`. Preserve alterações não commitadas; não use reset, checkout destrutivo ou limpeza para descartar trabalho existente.
- Mudanças em APIs públicas (`IOrmRepository`, `IOrmQueryExecutor`, `SqlQuery`, `OrmErrors`), atributos de auditoria (`[Auditable]`, `[SensitiveData]`, `[AuditIgnore]`), gerador Roslyn DTO, `ProtectedConnectionString`, dialetos de banco ou alvos .NET exigem revisão de compatibilidade, documentação, changelog e consumidores.
- Testes devem atravessar contratos públicos e verificar resultados observáveis. Para mudanças de comportamento, cubra entradas inválidas, limites, falhas e concorrência quando aplicável.
- Trate todo dado externo como não confiável. Não exponha segredos, PII ou conteúdo controlado pelo usuário em exceções, logs, URLs ou sumários.
- `ProtectedConnectionString` reduz o risco de vazar a string de conexão: `ToString()` retorna `"***"`, debugger e IntelliSense ocultam o valor, e o texto puro só sai por `Reveal()` (usado na atribuição ao driver ADO.NET). Não há criptografia em memória.
- Auditoria com diffs (`OldValues`, `NewValues`, `AffectedColumns`) é ativada por `[Auditable]`; `[SensitiveData]` mascara automaticamente nos diffs; `[AuditIgnore]` exclui da auditoria.

## Mapa da implementação

- `TEC.ORM/TEC.ORM/`: biblioteca `net10.0`, contratos fundamentais (AOT-ready, sem drivers de banco). O gerador Roslyn DTO é o projeto `TEC.ORM.Mapping.Generator`, empacotado dentro do `TEC.ORM`.
  - `Abstractions/`: `IOrmRepository<TEntity, TKey>`, `OrmRepositoryExtensions`, `IOrmQueryExecutor`, `[HardDelete]`.
  - `Queries/`: `SqlQuery`. `Common/`: `OrmErrors`. `Paging/`: `PageRequest`, `[Sortable]`. `Specifications/`: `Specification<T>`.
  - `Auditing/`: `IOrmAuditContextProvider`, `DefaultOrmAuditContextProvider`, `OrmAuditRecord`, atributos `[Auditable]`, `[SensitiveData]`, `[AuditIgnore]`.
  - `Entities/`: `Entity<TKey>`, `AuditableEntity<TKey>`. Os contratos `IEntity<TKey>`, `ISoftDelete` e `IAuditable` ficam no `TEC.Core.Domain` (o domínio dos consumidores referencia só o TEC.Core); a Fluent API equivalente aos atributos é `OrmMetadataBuilderExtensions` (`TEC.ORM.EntityFrameworkCore.Auditing`). `Mapping/`: `[MapFrom]` e demais atributos.
- `TEC.ORM.EntityFrameworkCore/`: núcleo agnóstico de banco, a única implementação do pipeline para os quatro provedores: registro comum (`AddTecOrmDbContext`), interceptores de conexão, exclusão lógica, autoria e trilha histórica, proteção de connection string, repositório genérico, executor Dapper, runner (`Diagnostics/`, com `ActivitySource`/`Meter` `TEC.ORM`), idempotência, convenções de modelo e abstrações de dialeto (`Dialects/`: `ISqlDialect`, `OrmExceptionTranslatorBase`). Os provedores só trazem o dialeto, o tradutor de exceções, o validador de leitura e o registro.
- `TEC.ORM.SqlServer/`: provedor especialista para Microsoft SQL Server e Azure SQL (`SqlServerDialect`, `SqlServerExceptionTranslator`, `AddTecOrm`/`UseTecOrm` e encaminhadores de idempotência e health check).
- `TEC.ORM.PostgreSql/`: provedor especialista para PostgreSQL (Npgsql).
- `TEC.ORM.MySql/`: provedor especialista para MySQL/MariaDB (`MySql.EntityFrameworkCore`; o Dapper usa o mesmo driver MySql.Data).
- `TEC.ORM.Oracle/`: provedor especialista para Oracle Database (`Oracle.EntityFrameworkCore`).
- `TEC.ORM.Mapping.Generator/`: gerador Roslyn do mapeamento entidade ↔ DTO (`netstandard2.0`).
- `TEC.ORM.Tests/`: testes unitários e de integração (a contagem sai do `dotnet test`; não fixe número aqui). O CI testa o banco real só no SQL Server. Os dialetos PostgreSQL, MySQL e Oracle têm testes unitários (`DialectTests`, `DialectSecurityTests`, `CommonPipelineTests`), de registro no container (`ProviderRegistrationTests`) e de integração (`Integration/ProviderIntegrationTests`), que se pulam sem a conexão de cada banco.
- `TEC.ORM.LoadTests/` e `TEC.ORM.Benchmarks/`: carga e benchmarks.
- `build/`: props/targets compartilhados.
- `docs/`: guias por API, segurança, testes, desenvolvimento. Não há `samples/` nem `docs/adr/` neste repositório.

## Contratos relevantes

- `Directory.Build.props` é a fonte única de versão do pacote (uma versão para todos os pacotes). A versão inicial é `0.0.1`; não introduza versões nos `.csproj`.
- `Directory.Packages.props` não tem versões por TFM: EF Core e provedores acompanham o .NET do alvo (`net10.0` → EF Core 10). A única condição é a do Roslyn, preso ao 4.12 no gerador.
- Dependências TEC.* são declaradas com `<TecReference Include="..." />`, não com `PackageReference` direto.
- `Entity<TKey>` não sobrescreve `Equals` nem `GetHashCode`: a igualdade é por referência. Se isso mudar, mantenha a compatibilidade durante a transição de entidade transiente para persistida e com proxies de ORM.
- Credenciais de banco resolvidas exclusivamente via `TEC.Vault` (`ISecretReader`); `ProtectedConnectionString` encapsula as credenciais.
- Native AOT: `TEC.ORM` (domínio, contratos e DTOs) é compatível; provedores EF Core marcam `<IsAotCompatible>false</IsAotCompatible>`.
- Workflows dos consumidores chamam `tudoemcodigo/tec-workflows`; confira as tags realmente publicadas antes de alterar referências.
- A licença é Apache-2.0. Mantenha `LICENSE`, `NOTICE`, badge/README e `<PackageLicenseExpression>` coerentes; o pacote inclui `NOTICE` quando presente.

## Documentação

- `README.md` e `CHANGELOG.md` têm conteúdo próprio do TEC.ORM: siga o padrão do template, mantenha a marca Tudo em Código em todos os READMEs e não substitua o conteúdo específico por placeholders genéricos.
- Atualize `README.md`, `CHANGELOG.md` e os guias em `docs/` quando uma mudança alterar comportamento ou contrato.
- Não existe pasta de ADRs hoje. Se registrar uma decisão transversal em ADR, crie `docs/adr/` com um índice e ligue-o em `docs/README.md`.
- Consulte `docs/revisao-tecnica.md` como relatório de auditoria com data e escopo; não trate resultados históricos como estado atual sem nova verificação.
- Workflows e configurações centrais do consumidor estão em `.github/workflows/`; mantenha `.github/workflows/README.md` fiel aos gatilhos, permissões e inputs reais.

## Verificação local

```powershell
dotnet restore TEC.ORM.slnx
dotnet build TEC.ORM.slnx -c Release
dotnet test --solution TEC.ORM.slnx -c Release
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"; dotnet test --solution TEC.ORM.slnx -c Release; Remove-Item Env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT
git diff --check
```

O CI também valida convenções, pacote e workflows reutilizáveis. Não afirme que o CI está funcional apenas com build/testes locais: confirme que as tags chamadas existem e que os `permissions` do chamador incluem o mínimo exigido pelo workflow reutilizável.