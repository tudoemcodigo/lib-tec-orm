<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# TEC.ORM.PostgreSql

Provedor oficial do **TEC.ORM** para **PostgreSQL** com Entity Framework Core (`Npgsql.EntityFrameworkCore.PostgreSQL`) e Dapper (`Npgsql`).

## Recursos

- **Configuração Simples via Container**: Extensão `AddTecOrmPostgreSql<TContext>()`, com o mesmo pipeline do SQL Server (identidade da auditoria, exclusão lógica, trilha histórica, convenções, logs sem dados sensíveis, `CommandTimeout` e mapeador de exceções do `TEC.Cqrs`).
- **Credenciais do Cofre**: Strings de conexão resolvidas via `TEC.Vault`. `SslMode` `Disable`, `Allow` e `Prefer` são recusados; `Require` cifra sem validar o certificado (Npgsql 6 ou superior) e só passa com `AllowTrustServerCertificate`; `VerifyCA` e `VerifyFull` passam.
- **Reserva Atômica de Idempotência**: `INSERT ... ON CONFLICT (...) DO NOTHING` com as colunas do modelo; escopo e chave com collation `"C"` (comparação binária).
- **Leituras Somente Leitura**: as consultas do Dapper rodam em `SET TRANSACTION READ ONLY`. Antes disso, o validador recusa DML e DDL, `FOR UPDATE`, `FOR SHARE`, `SET`, `DO`, `COPY` e funções com efeito colateral (`PG_SLEEP`, leitura e escrita de arquivos, `DBLINK` e variantes, as de *large object* que gravam como `LO_IMPORT` e `LO_UNLINK`, `SET_CONFIG`, `NEXTVAL`, `SETVAL`, `PG_ADVISORY_*`, `PG_TERMINATE_BACKEND`, `PG_CANCEL_BACKEND`, `PG_NOTIFY`...). Entende comentários aninhados, literais entre `$$` e nomes entre aspas.
- **Tradução de Erros Npgsql**: `SQLSTATE` 23505/23503 para conflito, 40001/40P01 para concorrência e 57014 para tempo esgotado, mais a lista de falhas de conexão e transitórias, também quando a exceção chega dentro de outra. Veja os detalhes em [Erros](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/erros.md).

> [!NOTE]
> **Suporte implementado, validação de integração pendente.** Os testes de integração com um PostgreSQL real existem
> (`TEC_TESTES_ORM_PG_CONEXAO`), mas ainda não foram executados. Detalhes em
> [Segurança](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/seguranca.md#provedores-postgresql-mysql-e-oracle).

## Instalação

```bash
dotnet add package TEC.ORM.PostgreSql --version 0.0.1
```

## Uso

```csharp
builder.Services.AddTecOrmPostgreSql<AppDbContext>(options =>
{
    options.ConnectionSecretName = "postgres-app-rw";
    options.ReadOnlyConnectionSecretName = "postgres-app-ro";
});
```

`net10.0` · EF Core 10 · [Apache 2.0](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/LICENSE) · Roberto Oliveira, equipe Tudo em Código
