<div align="center">

<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="TEC.ORM" width="100" />

# 🗄️ TEC.ORM

**Contratos de persistência independentes de banco e mapeamento entidade ↔ DTO gerado na compilação: o domínio e a aplicação usam o ORM sem depender de EF Core, Dapper ou SqlClient.**

[📚 Documentação](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/README.md) · [🔄 Mapeamento](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/mapeamento.md) · [🐙 Repositório](https://github.com/tudoemcodigo/lib-tec-orm)

</div>

## ✨ O que é

- **`IOrmRepository<TEntity, TKey>`**: CRUD para qualquer entidade e tipo de chave, busca, listagem paginada, existência,
  contagem e projeção para DTO, sempre com `Result` e os códigos de `OrmErrors`.
- **`IOrmQueryExecutor` + `SqlQuery`**: leituras complexas só com SQL parametrizado (cada valor interpolado vira parâmetro).
- **Entidades**: `Entity<TKey>`, `AuditableEntity<TKey>`, `IEntity<TKey>`, `ISoftDelete`, `IAuditable`.
- **Consultas**: `Specification<T>`, `PageRequest` e `[Sortable]` (lista branca da ordenação vinda da requisição).
- **Mapeamento gerado** (`[MapFrom]`, `[MapObject]`, `[MapList]`...): o gerador `TEC.ORM.Mapping.Generator` vem **dentro
  deste pacote** (`analyzers/dotnet/cs`) e escreve `Projection`, `FromEntity`, `ApplyTo` e `ToEntity`; mapeamento inválido
  é erro de build (`TECORM001`–`TECORM016`) e toda exclusão física gera o aviso `TECORM014`.
- Compatível com **Native AOT** e trimming; só depende do `TEC.Core`.

## 🎯 Quando usar

Projetos de domínio e de aplicação, e todo projeto que declara DTOs com `[MapFrom]`. A implementação fica num pacote por
banco, no projeto de infraestrutura/host:
[`TEC.ORM.SqlServer`](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/TEC.ORM.SqlServer/README.md),
[`TEC.ORM.PostgreSql`](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/TEC.ORM.PostgreSql/README.md),
[`TEC.ORM.MySql`](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/TEC.ORM.MySql/README.md) ou
[`TEC.ORM.Oracle`](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/TEC.ORM.Oracle/README.md).

## 📥 Instalação

```bash
dotnet nuget add source https://nuget.pkg.github.com/tudoemcodigo/index.json -n tec-interno -u <usuario> -p <PAT>
dotnet add package TEC.ORM --version 0.0.1
```

> [!IMPORTANT]
> **Use o pacote do seu banco (`TEC.ORM.SqlServer`, `TEC.ORM.PostgreSql`, `TEC.ORM.MySql` ou `TEC.ORM.Oracle`) e o
> `TEC.ORM.EntityFrameworkCore` na mesma versão deste pacote.** Os satélites usam tipos internos do núcleo
> (`InternalsVisibleTo`) e todos são publicados juntos; versões diferentes podem falhar em execução
> (`MissingMethodException`, `TypeLoadException`). Requer o .NET SDK 10 (o pacote é só `net10.0`; o Roslyn 4.12 é apenas o mínimo do gerador).

## 🚀 Início rápido

```csharp
using TEC.Core.Common.Results;
using TEC.ORM.Abstractions;
using TEC.ORM.Entities;
using TEC.ORM.Mapping;

public sealed class Customer : Entity<Guid>              // Id + exclusão lógica
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

[MapFrom(typeof(Customer))]
public partial class CustomerDto                          // o gerador completa a classe
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class CustomerService(IOrmRepository<Customer, Guid> customers)
{
    public Task<Result<CustomerDto>> GetAsync(Guid id, CancellationToken ct) =>
        customers.GetByIdAsync<CustomerDto>(id, ct);     // SELECT só das colunas do DTO; ORM_NAO_ENCONTRADO se excluído
}
```

## 📚 Documentação

Repositório, especificações, consultas SQL, mapeamento e diagnósticos:
[docs/README.md](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/README.md).

`net10.0` · Native AOT · [Apache 2.0](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/LICENSE) · Roberto Oliveira, equipe Tudo em Código
