# 📝 Changelog

Todas as mudanças relevantes do **TEC.ORM** são registradas aqui. O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa [Versionamento Semântico](https://semver.org/lang/pt-BR/). Enquanto a versão for `0.x`, mudanças incompatíveis podem ocorrer em versões MINOR. Os dois pacotes (`TEC.ORM`, que leva o gerador, e `TEC.ORM.SqlServer`) saem sempre juntos, com a mesma versão, e devem ser usados na mesma versão (o satélite usa internos do núcleo).

## [0.2.0] - Não publicado

Correções da [revisão técnica de 2026-10-19](docs/revisao-tecnica.md). MINOR (0.x) por trazer mudanças de comportamento incompatíveis, marcadas com ⚠️.

### 🔒 Segurança

- **Dependência vulnerável (ORG-01):** `System.Security.Cryptography.Pkcs` fixado em 10.0.13 no `net10.0` (transitivo do `Microsoft.Data.SqlClient` 6.1.6, que trazia a 9.0.11 com GHSA-846f-f764-76mx e GHSA-2fmv-rv2c-59x7). O build volta a passar com a auditoria NuGet ligada.
- **Valor de coluna no log 3005 (SEG-01):** exceções de conversão (`FormatException`, `InvalidCastException`, `OverflowException`, `DataException` do Dapper) são registradas sem a mensagem (só tipos e pilha).
- **Autoria da exclusão física (SEG-02):** o log 3007 passa a ter `{Actor}` (id do `ICurrentUser`, conforme `IdentifierLogMode`) e `{Rows}` (linhas removidas, inclusive no lote); `OrmOperation` ganhou `Actor` e `AffectedRows`. ⚠️ `HardDeleteAsync` de entidade `IAuditable` sem identidade autenticada é recusado com `ORM_AUDITORIA_SEM_IDENTIDADE`, como as demais gravações.
- ⚠️ **`SortBy` falha fechada (SEG-03):** entidade sem `[Sortable]` aceita só a chave e `CreatedAt`/`UpdatedAt`. `OrmOptions.AllowUnmarkedSortBy = true` volta ao comportamento anterior (qualquer propriedade escalar). Justificativa: `?sort=PasswordHash` vindo da API permitia inferir valores pela ordem.
- **Vários resultados (SEG-04):** o executor recusa um segundo resultado (`SELECT 1 SELECT 2` sem `;`) com `ORM_CONSULTA_NAO_PERMITIDA`, cancelando o comando (antes era drenado fora do `MaxQueryRows`); a verificação de somente leitura recusa `UPDLOCK`, `XLOCK`, `TABLOCKX` e `HOLDLOCK`.
- **`IN` e `LIKE` (SEG-05):** cada item de lista conta no limite de 2.000 parâmetros do `SqlQuery`; novo `SqlQuery.EscapeLike(string)` para `LIKE ... ESCAPE '\'`.
- ⚠️ **`IdentifierLogMode.Auto` como padrão (SEG-06):** inteiros e `Guid` em claro; `string` e demais tipos com HMAC. Para o comportamento anterior, `IdentifierLogMode = Plain`.

### 🐛 Corrigido

- **`TransactionScope` (BUG-01):** a conexão de leitura do `IOrmQueryExecutor` tem `Enlist=false`: dentro de um `TransactionScope` com o EF Core, a transação não é mais promovida a distribuída (falhava no Linux).
- **Propagação da exclusão lógica (BUG-02):** teto `OrmOptions.MaxSoftDeletePropagation` (padrão 10.000; acima, `ORM_LIMITE_EXCEDIDO` sem gravar nada e sem carregar além do teto) e dependentes rastreados achados por índice de chave estrangeira (antes, varredura O(n²) do rastreador). ⚠️ Exclusões que propagavam para mais de 10.000 dependentes passam a falhar. Novo `OrmErrors.SoftDeletePropagationTooLarge`.
- **Corrida no `UpdateAsync` (BUG-03):** `IsDeleted` é token de concorrência (convenção, sem mudança de schema): exclusão concorrente entre a verificação e o `UPDATE` resulta em `ORM_CONCORRENCIA`, sem alterar o registro excluído.
- **Autoria devolvida pelo `UpdateAsync` (BUG-04):** em entidade `IAuditable`, os campos de criação e de exclusão devolvidos são os do banco (lidos na própria verificação de existência, sem consulta extra).
- **Dependentes criados já excluídos (BUG-05):** `CreateAsync` zera `IsDeleted`/`DeletedAt` de todo dependente novo do grafo, não só da raiz.
- ⚠️ **Paginação profunda (BUG-06):** `OrmOptions.MaxPageOffset` (padrão 10.000): página que pularia mais itens é `ORM_ENTRADA_INVALIDA` antes de ir ao banco.
- **`TECORM014` (IMP-01):** também avisa quando `HardDeleteAsync` é referenciado como *method group*.
- **Índice do `HasSoftDelete` (IMP-02):** o filtro usa o nome real da coluna (`HasColumnName`), aplicado pela convenção; documentado o padrão de índice útil (o padrão `index: true` foi mantido para não gerar migration de remoção).
- **Lista branca de ordenação (IMP-03):** calculada uma vez por modelo.
- ⚠️ **Idempotência (contrato do TEC.Cqrs):** colunas `Scope` e `IdempotencyKey` com collation binária `Latin1_General_100_BIN2` (comparação ordinal). Tabelas existentes precisam de migração manual da chave primária ([🔁 Idempotência](docs/idempotencia.md)).

### 🔧 Interno

- Imagem do SQL Server da integração fixada por digest (`2022-CU27-GDR1-ubuntu-22.04`); `TEC.Vault` 0.0.1 → 0.1.0.

## [0.1.0] - 2026-10-09

### ✨ Adicionado

#### 🗃️ TEC.ORM.SqlServer

- **Idempotência no banco:** `EfIdempotencyStore<TContext>` implementa o `IIdempotencyStore` do TEC.Cqrs 0.1.0 (usado pelo `UseTecIdempotency`), com reserva atômica (`INSERT ... WHERE NOT EXISTS` com `UPDLOCK, HOLDLOCK`), tomada de reservas vencidas condicionada ao `LockId` anterior e sem `SaveChanges` (não grava alterações pendentes da aplicação). `modelBuilder.AddTecIdempotency(schema, tableName)` mapeia a tabela; `services.AddTecOrmIdempotency<TContext>()` registra o store (Scoped) e a limpeza periódica em lotes (`OrmIdempotencyOptions`, logs 3200–3201).
- **Concorrência fora do repositório:** o `AddTecOrm` registra um `IExceptionErrorMapper` que converte `DbUpdateConcurrencyException` e deadlock em `ORM_CONCORRENCIA` e violação de chave em `ORM_CONFLITO` (409), inclusive quando lançados no commit do `IUnitOfWork` (`TransactionBehavior`) ou por um `DbContext` usado direto. Antes, subiam como 500.
- **Circuit breaker da abertura de conexões** (`OrmOptions.CircuitBreaker`, `Polly.Core` 8.8.0, ligado por padrão): um circuito por contexto e tipo de conexão; com o banco fora do ar as aberturas falham na hora com `ORM_CONEXAO_INDISPONIVEL`, sem esperar o `Connect Timeout` e sem repetir nada (vale também para escritas). No EF Core o interceptor abre a conexão pelo circuito e suprime a abertura do EF; conexões com o `master` (criação do banco) passam fora dele. Na abertura de teste só uma conexão aberta fecha o circuito. Métrica `orm.circuit.state_changes` e eventos 3105–3108. Nova dependência: `Polly.Core`.

### 🐛 Corrigido

- O aviso de `TrustServerCertificate` habilitado sai uma vez por tipo de conexão (leitura/escrita e somente leitura), e não a cada conexão aberta: em desenvolvimento, inundava o log.

## [0.0.1] - 2026-10-08

Primeira versão publicada.

### ✨ Adicionado

#### 🗄️ TEC.ORM (abstrações, compatível com Native AOT, depende só do TEC.Core)

- **`IOrmRepository<TEntity, TKey>`**: criar, ler, atualizar, excluir (lógica), buscar, listar paginado, existência e contagem para qualquer entidade e tipo de chave, sempre com `Result`; métodos `<TDto>` (`GetByIdAsync`, `FindAsync`, `ListAsync`) com projeção no banco só das colunas do DTO.
- **Exclusão física explícita**: `HardDeleteAsync(TKey)` e `HardDeleteAsync(ISpecification<TEntity>)` (lote, critério obrigatório), marcadas com `[HardDelete]`; atalhos com expressão em `OrmRepositoryExtensions` (`FindAsync`, `ExistsAsync`, `CountAsync`, `HardDeleteAsync`).
- **`IOrmQueryExecutor`** para leituras complexas (`QueryAsync`, `QuerySingleOrDefaultAsync`, `ExecuteScalarAsync` e join com `splitOn`) e **`SqlQuery`** sempre parametrizada (`Interpolated`, `Create` com objeto ou dicionário), com `Parameters` somente leitura, limites de tamanho e de parâmetros e `ToString` sem SQL nem valores.
- **Entidades**: `IEntity<TKey>`, `ISoftDelete`, `IAuditable`, `Entity<TKey>` e `AuditableEntity<TKey>`.
- **Consultas**: `ISpecification<T>`/`Specification<T>`, `PageRequest` validado e `[Sortable]` (lista branca do `SortBy`; sombra, exclusão lógica e tokens de concorrência sempre recusados).
- **`OrmErrors`**: códigos padronizados (`ORM_ENTRADA_INVALIDA`, `ORM_NAO_ENCONTRADO`, `ORM_CONFLITO`, `ORM_CONCORRENCIA`, `ORM_LIMITE_EXCEDIDO`, `ORM_CONSULTA_NAO_PERMITIDA`, `ORM_CONEXAO_INDISPONIVEL`, `ORM_CONEXAO_INVALIDA`, `ORM_TEMPO_ESGOTADO`, `ORM_AUDITORIA_SEM_IDENTIDADE`, `ORM_FALHA`), com `QueryTooManyRows(int)` para o teto das leituras complexas; mensagens sem dados sensíveis.

#### 🔄 Mapeamento entidade ↔ DTO (gerador `TEC.ORM.Mapping.Generator`, dentro do pacote `TEC.ORM`)

- Atributos `[MapFrom]` (com achatamento seguro contra nulos), `[MapObject]`, `[MapList]` (`MaxDepth`, `Sync`), `[MapIgnore]` (`MapDirection`), `[MapReadOnly]` e `[MapConverter]` (`IMapConverter`/`IBidirectionalMapConverter`).
- Gerados por DTO: `Projection`, `FromEntity`, `ApplyTo`, `ToEntity` e extensões `To{Dto}`, `To{Dto}List`, `ProjectTo{Dto}`; contratos `IDtoMap`, `IDtoReverseMap`, `IDtoEntityFactory` e atalhos `ProjectToDto`, `ToDtoList`, `ToDtoPage`.
- Volta protegida contra *overposting* (nunca `Id`, exclusão lógica, auditoria, achatados ou somente leitura) e coleções com `Sync` sincronizadas pelo `Id`.
- Código gerado com `// <auto-generated/>` e `#nullable enable`; `[MapFrom]` repetido em várias declarações `partial` gera só uma vez.
- Diagnósticos de build `TECORM001`–`TECORM013`, `TECORM015` (entidade não suportada: genérico aberto ou tipo não encontrado) e `TECORM016` (falha interna relatada como erro do DTO, sem derrubar o gerador); aviso `TECORM014` em toda chamada de exclusão física.

#### 🗃️ TEC.ORM.SqlServer (EF Core + Dapper; sem Native AOT)

- **`AddTecOrm<TContext>`** com opções validadas na subida, vários contextos (cada um com segredo, política e tempo limite próprios) e **`UseTecOrm`** para `AddDbContextFactory` ou contextos montados à mão.
- **`OrmRepository<TEntity, TKey>`** (EF Core 8 e 10): escrita imediata, atualização só da raiz com concorrência otimista, exclusão lógica propagada, exclusão física por `ExecuteDeleteAsync` com log de auditoria em `Warning`, membros `virtual` para especializar.
- **`OrmQueryExecutor`** (Dapper, conexão somente leitura): verificação de somente leitura validada por fuzzing diferencial, leitura em fluxo com `MaxQueryRows` e cancelamento do comando no servidor; `QuerySingleOrDefault` lê no máximo 2 linhas, o escalar só a primeira (`NULL` em tipo valor vira `default`); join sem a coluna `splitOn` devolve `ORM_ENTRADA_INVALIDA`.
- **Conexão sempre do cofre** (`IOrmConnectionSecurity`/`OrmConnectionSecurity` com o TEC.Vault): política com criptografia obrigatória, `TrustServerCertificate` só com opt-in, `Persist Security Info=False`, `Application Name` e `ApplicationIntent=ReadOnly` na leitura.
- **Exclusão lógica** obrigatória por convenção (`OrmDbContext`, `AddTecOrmConventions`, `HasSoftDelete`), com filtro nomeado `TecOrm.SoftDelete` no EF Core 10.
- **Auditoria** com o `ICurrentUser` do TEC.Core (`OrmIdentity`, `UseTecOrmIdentity`): autoria e tenant por operação, falha fechada sem identidade; no `UseTecOrm`, cópia imutável da identidade resolvida a cada gravação.
- **Resiliência**: `OrmOptions.TransientRetryCount` (0 a 5, padrão 2) e `TransientRetryDelay` (10 ms a 10 s, padrão 200 ms): novas tentativas só em leituras fora de transação, com espera exponencial e variação aleatória, log 3008 e tag `orm.retries`; escritas, operações em transação e tempo esgotado nunca são repetidos (`OrmOperation.IsRetryable`). Pool esgotado e erros transitórios do Azure SQL (40613, 40501, 10928...) viram `ORM_CONEXAO_INDISPONIVEL`.
- **Limites**: `MaxPageSize`, `MaxFindResults`, `MaxQueryRows` (1 a 1.000.000, padrão 10.000) e `CommandTimeoutSeconds`.
- **`OrmUnitOfWork`** (`IUnitOfWork` do TEC.Cqrs): transação por command no pipeline, command aninhado que falha desfaz o externo; rastreador limpo em rollback ou commit que falha.
- **Observabilidade** (`IOrmOperationRunner`/`OrmOperationRunner`, com `TimeProvider` opcional): `ActivitySource` e `Meter` `TEC.ORM` (`orm.operation.duration`), logs de auditoria 3000–3008 e 3100–3104 sem SQL, valores ou mensagem do banco; eventos de erro do EF Core que repetiriam a mensagem do SQL Server desligados; identificador no log em `Plain` (controle, formatação como U+202E e U+2028/U+2029 viram `?`), `Hashed` (`<N caracteres, hmac:…>`) ou `Omitted`.
- **Health check** `AddHealthChecks().AddTecOrm()` (tags `ready` e `database`), só com o código do erro na descrição.

#### 🧪 Qualidade

- `TEC.ORM.Tests` (TUnit): 288 testes unitários em `net10.0` e 286 em `net8.0` (também sem ICU), com fuzzing, fuzzing diferencial contra o parser do T-SQL, DoS, vazamento e gerador sobre código compilado em memória; 40 testes de integração com SQL Server real (+1 com o Key Vault de testes, só na `main`), categoria `Integracao`.
- `TEC.ORM.LoadTests`: 13 testes `Carga-CI` e 5 `Carga-Pesada` (sustentada, tempestade no pool, escalabilidade, volume e soak) no schema `loadtest`, com `TEC_CARGA_SOAK_SEGUNDOS`, `TEC_CARGA_DURACAO_SEGUNDOS`, `TEC_CARGA_CONCORRENCIA`, `TEC_CARGA_LINHAS` e relatórios em `TEC_CARGA_RELATORIOS`; `Seguranca-Pesada` com 200 mil lotes no fuzzing diferencial.
- `TEC.ORM.Benchmarks` (BenchmarkDotNet): custo de cada operação em relação ao EF Core puro, consultas, observabilidade e mapeamento.
- Banco de testes code first (schema `codefirst`, migration `InitialDbTecBase`) e database first (schema `dbfirst`, `scaffold.ps1`); conexão por `TEC_TESTES_ORM_SQL_CONEXAO` (cofre em memória) ou `TEC_TESTES_ORM_SQL_SEGREDO` com `TEC_TESTES_VAULT_URI` (Key Vault).
- CI/CD pelos workflows reutilizáveis do tec-workflows: `ci.yml` (integração com SQL Server em container e, na `main`, segredo temporário no Key Vault via OIDC), `release.yml` (com os pesados como portão) e `performance.yml` (semanal/manual, com parâmetros de carga e benchmarks opcionais).

[0.0.1]: https://github.com/tudoemcodigo/lib-tec-orm
