<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# TEC.ORM.MySql

Provedor oficial do **TEC.ORM** para **MySQL** e **MariaDB** com Entity Framework Core e Dapper.

## Recursos

- **Configuração Simples via Container**: Extensão `AddTecOrmMySql<TContext>()`.
- **Credenciais do Cofre**: Strings de conexão resolvidas via `TEC.Vault` com `SslMode` obrigatório (`VerifyCA` ou `VerifyFull`).
- **Reserva Atômica de Idempotência**: Cláusula nativa `INSERT IGNORE INTO`.
- **Validador Somente Leitura**: Bloqueio de DML, `LOCK IN SHARE MODE`, `FOR UPDATE`, comentários executáveis (`/*! ... */`) e funções do sistema (`LOAD_FILE`, `SLEEP`).
- **Tradução Híbrida de Exceções**: Compatibilidade transparente entre drivers `MySqlConnector` (.NET 8) e Oracle MySQL (.NET 10).

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
