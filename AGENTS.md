# Instruções para agentes — TEC.ORM

Este arquivo complementa as instruções herdadas de `AGENT_BASE.md`. O TEC.ORM fornece persistência padronizada, agnóstica e segura por padrão: CRUD para qualquer entidade, consultas SQL via Dapper, suporte a SQL Server, PostgreSQL, MySQL e Oracle, trilha histórica de auditoria com diffs, exclusão lógica e conversão entidade ↔ DTO gerada na compilação, com credenciais lidas exclusivamente de cofres.

## Escopo e segurança de alterações

- O escopo padrão é este repositório. Não altere outros repositórios `TEC.*`, não publique pacotes/tags/releases e não rode sincronizações que escrevam fora daqui sem pedido explícito.
- Antes de editar, confira `git status` e `git diff`. Preserve alterações não commitadas; não use reset, checkout destrutivo ou limpeza para descartar trabalho existente.
- Mudanças em APIs públicas (`IOrmRepository`, `IOrmQueryExecutor`, `SqlQuery`, `OrmErrors`), atributos de auditoria (`[Auditable]`, `[SensitiveData]`, `[AuditIgnore]`), gerador Roslyn DTO, `ProtectedConnectionString`, dialetos de banco ou alvos .NET exigem revisão de compatibilidade, documentação, changelog e consumidores.
- Testes devem atravessar contratos públicos e verificar resultados observáveis. Para mudanças de comportamento, cubra entradas inválidas, limites, falhas e concorrência quando aplicável.
- Trate todo dado externo como não confiável. Não exponha segredos, PII ou conteúdo controlado pelo usuário em exceções, logs, URLs ou sumários.
- `ProtectedConnectionString` garante que a string de conexão nunca vaze: `ToString()` retorna `"***"`, debugger ocultado, IntelliSense ocultado, descriptografada apenas no driver ADO.NET no momento estrito da atribuição.
- Auditoria com diffs (`OldValues`, `NewValues`, `AffectedColumns`) é ativada por `[Auditable]`; `[SensitiveData]` mascara automaticamente nos diffs; `[AuditIgnore]` exclui da auditoria.

## Mapa da implementação

- `TEC.ORM/`: biblioteca multi-target (`net8.0` e `net10.0`), contratos fundamentais e gerador Roslyn DTO (AOT-ready, sem drivers de banco).
  - `Abstractions/`: `IOrmRepository<TEntity, TKey>`, `IOrmQueryExecutor`, `SqlQuery`, `OrmErrors`, `OrmAuditRecord`, atributos `[Auditable]`, `[SensitiveData]`, `[AuditIgnore]`, `[MapFrom]`.
  - `Entities/`: `AuditableEntity<TId>`, `DomainEntity<TId>`.
  - `SourceGenerators/`: gerador Roslyn para projeção entidade ↔ DTO compilada em tempo de compilação sem reflexão.
  - `Telemetry/`: `ActivitySource` e `Meter` `TEC.ORM` da BCL; assinados pelo TEC.Observability via prefixo `TEC.*`.
- `TEC.ORM.EntityFrameworkCore/`: núcleo base agnóstico: interceptor de auditoria com diffs, proteção de connection string, repositório genérico, executor Dapper, idempotência e abstrações de dialetos.
- `TEC.ORM.SqlServer/`: provedor especialista para Microsoft SQL Server e Azure SQL.
- `TEC.ORM.PostgreSql/`: provedor especialista para PostgreSQL (Npgsql).
- `TEC.ORM.MySql/`: provedor especialista para MySQL/MariaDB (Pomelo no .NET 8, MySql.EntityFrameworkCore no .NET 10, MySqlConnector).
- `TEC.ORM.Oracle/`: provedor especialista para Oracle Database (`Oracle.EntityFrameworkCore`).
- `TEC.ORM.Mapping.Generator/`: gerador de mapeamento adicional.
- `TEC.ORM.Tests/`: 364 testes unitários no .NET 10 e 362 no .NET 8 (100% sucesso); cobertura completa de trilha histórica, diffs, atores (User, M2M, ApiKey, System), mascaramento sensível, proteção de credenciais e dialetos.
- `TEC.ORM.LoadTests/` e `TEC.ORM.Benchmarks/`: carga e benchmarks.
- `samples/`: exemplos de uso.
- `build/`: props/targets compartilhados e polyfills necessários ao `net8.0`.
- `docs/adr/`: decisões arquiteturais.
- `docs/`: guias por API, segurança, compatibilidade, testes, desenvolvimento.

## Contratos relevantes

- `Directory.Build.props` é a fonte única de versão do pacote (uma versão para todos os pacotes). A versão inicial é `0.0.1`; não introduza versões nos `.csproj`.
- `Directory.Packages.props` pode declarar versões distintas por TFM para `Microsoft.Extensions.*`; condições por TFM são intencionais.
- Dependências TEC.* são declaradas com `<TecReference Include="..." />`, não com `PackageReference` direto.
- `DomainEntity<TId>` deve manter `Equals` e `GetHashCode` compatíveis durante a transição de entidade transiente para persistida e com proxies de ORM.
- Credenciais de banco resolvidas exclusivamente via `TEC.Vault` (`ISecretReader`); `ProtectedConnectionString` encapsula as credenciais.
- Native AOT: `TEC.ORM` (domínio, contratos e DTOs) é compatível; provedores EF Core marcam `<IsAotCompatible>false</IsAotCompatible>`.
- Workflows dos consumidores chamam `tudoemcodigo/tec-workflows`; confira as tags realmente publicadas antes de alterar referências.
- A licença é Apache-2.0. Mantenha `LICENSE`, `NOTICE`, badge/README e `<PackageLicenseExpression>` coerentes; o pacote inclui `NOTICE` quando presente.

## Documentação

- `README.md` e `CHANGELOG.md` têm conteúdo próprio do TEC.ORM: siga o padrão do template, mantenha a marca Tudo em Código em todos os READMEs e não substitua o conteúdo específico por placeholders genéricos.
- Atualize `README.md`, `CHANGELOG.md` e os guias em `docs/` quando uma mudança alterar comportamento ou contrato.
- Registre decisões transversais em um ADR numerado e mantenha `docs/adr/README.md` e `docs/README.md` atualizados.
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