<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# TEC.ORM.Oracle

Provedor oficial do **TEC.ORM** para **Oracle Database** com Entity Framework Core (`Oracle.EntityFrameworkCore`) e Dapper (`Oracle.ManagedDataAccess.Core`).

## Recursos

- **Configuração Simples via Container**: Extensão `AddTecOrmOracle<TContext>()`, com o mesmo pipeline do SQL Server (identidade da auditoria, exclusão lógica, trilha histórica, convenções, logs sem dados sensíveis, `CommandTimeout` e mapeador de exceções do `TEC.Cqrs`). O parâmetro opcional `oracle` ajusta o provedor do EF Core, por exemplo `o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19)` num banco anterior ao 23ai (o DDL padrão do provedor usa `BOOLEAN`).
- **Credenciais do Cofre**: Strings de conexão resolvidas via `TEC.Vault`. TLS obrigatório: num descritor, todo `ADDRESS` precisa de `PROTOCOL=TCPS` (espaços, tabulações e quebras de linha são normalizados antes da verificação); EZConnect precisa começar com `tcps://`; `SSL_SERVER_DN_MATCH` desligado conta como certificado não validado. O que não passa só é aceito com `AllowTrustServerCertificate`. Alias do `tnsnames` não é inspecionável e fica sob a política de quem o mantém. A conexão de leitura recebe `Enlist=false`; o `Connection Timeout` do segredo não é alterado.
- **Consultas Dapper**: os marcadores do `SqlQuery.Interpolated` saem como `:p0`, `:p1`...; no `SqlQuery.Create`, escreva `:Nome`. As consultas rodam em `SET TRANSACTION READ ONLY`.
- **Reserva Atômica de Idempotência**: `MERGE INTO ... USING (SELECT ... FROM DUAL)` com as colunas do modelo; o corpo da resposta vai em `BLOB`.
- **Trilha Histórica**: `OldValues`, `NewValues` e `AffectedColumns` em `NCLOB` (o padrão do provedor, `NVARCHAR2(2000)`, faria o salvamento falhar com alterações maiores).
- **Validador Somente Leitura**: Recusa DML e DDL, `FOR UPDATE`, `DECLARE`, `BEGIN`, entende literais `q'[...]'` e recusa `DBMS_*`, `UTL_*` e `HTTPURITYPE` em qualquer parte do nome (`SYS.DBMS_...`, `"DBMS_LOCK"`).
- **Tradução de Erros ODP.NET**: Mapeamento de códigos ORA para conflito, concorrência, tempo esgotado e falhas de conexão, também quando a exceção chega dentro de outra.

> [!NOTE]
> **Suporte implementado, validação de integração pendente.** Os testes de integração com um Oracle real existem
> (`TEC_TESTES_ORM_ORACLE_CONEXAO`, listener `TCPS`), mas ainda não foram executados. Detalhes em
> [Segurança](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/docs/seguranca.md#provedores-postgresql-mysql-e-oracle).

## Instalação

```bash
dotnet add package TEC.ORM.Oracle --version 0.0.1
```

## Uso

```csharp
builder.Services.AddTecOrmOracle<AppDbContext>(options =>
{
    options.ConnectionSecretName = "oracle-app-rw";
    options.ReadOnlyConnectionSecretName = "oracle-app-ro";
});
```

`net10.0` · EF Core 10 · [Apache 2.0](https://github.com/tudoemcodigo/lib-tec-orm/blob/main/LICENSE) · Roberto Oliveira, equipe Tudo em Código
