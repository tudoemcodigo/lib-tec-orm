-- Schema "loadtest" dos testes de carga do TEC.ORM (modelo em LoadModel.cs). Idempotente e num único lote: a fixture executa
-- antes do primeiro teste com banco. sp_getapplock serializa a criação entre processos (net8.0 e net10.0 rodam em paralelo).
-- Cada teste grava com um "Batch" próprio e apaga as suas linhas no fim: as tabelas não crescem entre execuções.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
EXEC sp_getapplock @Resource = N'tec-orm-loadtest-schema', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 60000;

IF SCHEMA_ID(N'loadtest') IS NULL
    EXEC (N'CREATE SCHEMA loadtest');

IF OBJECT_ID(N'loadtest.Accounts', N'U') IS NULL
BEGIN
    CREATE TABLE loadtest.Accounts
    (
        Id              uniqueidentifier NOT NULL CONSTRAINT PK_Accounts PRIMARY KEY,
        Batch            uniqueidentifier NOT NULL,
        Name            nvarchar(150)    NOT NULL,
        Email           nvarchar(254)    NOT NULL,
        Category       int              NOT NULL,
        Balance           decimal(18, 2)   NOT NULL,
        Version          rowversion       NOT NULL,
        IsDeleted       bit              NOT NULL CONSTRAINT DF_Accounts_IsDeleted DEFAULT (0),
        DeletedAt       datetimeoffset   NULL,
        CreatedAt       datetimeoffset   NOT NULL,
        CreatedBy       nvarchar(256)    NULL,
        CreatedByTenant nvarchar(64)     NULL,
        UpdatedAt       datetimeoffset   NULL,
        UpdatedBy       nvarchar(256)    NULL,
        UpdatedByTenant nvarchar(64)     NULL,
        DeletedBy       nvarchar(256)    NULL,
        DeletedByTenant nvarchar(64)     NULL
    );
    -- Único só entre os não excluídos (como no code first): disputa de e-mail nos testes de concorrência
    CREATE UNIQUE INDEX UX_Accounts_Email ON loadtest.Accounts (Email) WHERE IsDeleted = 0;
    CREATE INDEX IX_Accounts_Batch ON loadtest.Accounts (Batch, Category) INCLUDE (Name, Balance, IsDeleted);
END;

IF OBJECT_ID(N'loadtest.LedgerEntries', N'U') IS NULL
BEGIN
    CREATE TABLE loadtest.LedgerEntries
    (
        Id        bigint IDENTITY (1, 1) NOT NULL CONSTRAINT PK_LedgerEntries PRIMARY KEY,
        Batch      uniqueidentifier NOT NULL,
        AccountId   uniqueidentifier NOT NULL CONSTRAINT FK_LedgerEntries_Accounts REFERENCES loadtest.Accounts (Id),
        Amount     decimal(18, 2)   NOT NULL,
        PostedAt  datetimeoffset   NOT NULL,
        Description nvarchar(200)    NOT NULL,
        IsDeleted bit              NOT NULL CONSTRAINT DF_LedgerEntries_IsDeleted DEFAULT (0),
        DeletedAt datetimeoffset   NULL
    );
    CREATE INDEX IX_LedgerEntries_Account ON loadtest.LedgerEntries (AccountId) INCLUDE (Amount, IsDeleted);
    CREATE INDEX IX_LedgerEntries_Batch ON loadtest.LedgerEntries (Batch);
END;

COMMIT;
