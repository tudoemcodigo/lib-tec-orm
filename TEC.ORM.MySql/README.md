<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# TEC.ORM.MySql

Provedor oficial do **TEC.ORM** para **MySQL** e **MariaDB** com Entity Framework Core (`MySql.EntityFrameworkCore`) e Dapper, os dois sobre o driver MySql.Data.

## Recursos

- **Configuração Simples via Container**: Extensão `AddTecOrmMySql<TContext>()`, com o mesmo pipeline do SQL Server (identidade da auditoria, exclusão lógica, trilha histórica, convenções, logs sem dados sensíveis, `CommandTimeout` e mapeador de exceções do `TEC.Cqrs`). O parâmetro opcional `mySql` ajusta o provedor do EF Core. Não há versão do servidor a configurar: o `MySql.EntityFrameworkCore` não tem essa opção.
- **Credenciais do Cofre**: Strings de conexão resolvidas via `TEC.Vault`. `SslMode=Disabled` (ou `None`) é recusado; `Preferred` e `Required` não validam o certificado e só passam com `AllowTrustServerCertificate`; `VerifyCA` e `VerifyFull` passam. A conexão recebe `AllowLoadLocalInfile=false` e `Persist Security Info=False`. `OrmOptions.ApplicationName` não é aplicado (o MySql.Data não tem a chave).
- **Reserva Atômica de Idempotência**: `INSERT` simples na chave primária; só a duplicata (erro 1062 ou 1586) conta como "chave já existe". Escopo e chave com `utf8mb4_bin` (comparação binária), declarada pelas anotações do `MySql.EntityFrameworkCore`.
- **Leituras Somente Leitura**: as consultas do Dapper rodam em `START TRANSACTION READ ONLY` (desfeita no fim; a conexão de leitura usa `ConnectionReset=true`). Antes disso, o validador recusa DML e DDL, `LOCK IN SHARE MODE`, `FOR UPDATE`, `FOR SHARE`, `SET`, comentários executáveis (`/*! ... */`, `/*M! ... */`), `LOAD_FILE`, `SLEEP`, `BENCHMARK` e as funções de trava (`GET_LOCK` e afins). `REPLACE()`, `TRUNCATE()` e `INSERT()` usados como funções passam.
- **Tradução de Exceções**: Lê o número do erro de `MySql.Data.MySqlClient.MySqlException`, também quando ela chega dentro de outra exceção.

> [!NOTE]
> **Suporte implementado, validação de integração pendente.** Os testes de integração com um MySQL real existem
> (`TEC_TESTES_ORM_MYSQL_CONEXAO`), mas ainda não foram executados. O provedor grava `DateTimeOffset` em `datetime` (UTC,
> sem fração de segundo). Detalhes em
> [Segurança](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/seguranca.md#provedores-postgresql-mysql-e-oracle).

## Instalação

```bash
dotnet add package TEC.ORM.MySql --version 0.0.1
```

## Uso

```csharp
builder.Services.AddTecOrmMySql<AppDbContext>(options =>
{
    options.ConnectionSecretName = "mysql-app-rw";
    options.ReadOnlyConnectionSecretName = "mysql-app-ro";
});
```

`net10.0` · EF Core 10 · [Apache 2.0](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/LICENSE) · Roberto Oliveira, equipe Tudo em Código
