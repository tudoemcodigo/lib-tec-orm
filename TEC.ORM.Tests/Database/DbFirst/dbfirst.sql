-- =====================================================================================================================
-- Banco de testes — opção 2: database first (schema "dbfirst")
--
-- O banco é a fonte da verdade: este script cria (de forma idempotente) as tabelas, e as classes em Generated\ são o
-- resultado do scaffolding (dotnet ef dbcontext scaffold; ver scaffold.ps1). As classes em Partials\ ligam o modelo gerado
-- ao TEC.ORM (IEntity<TKey> + ISoftDelete) e não são sobrescritas ao gerar de novo.
--
-- Toda tabela tem exclusão lógica: IsDeleted (bit, padrão 0) + DeletedAt (datetimeoffset, nulo).
-- Executado pela fixture de integração (lotes separados por GO), com a conexão obtida do TEC.Vault.
-- =====================================================================================================================

IF SCHEMA_ID(N'dbfirst') IS NULL
    EXEC(N'CREATE SCHEMA dbfirst');
GO

IF OBJECT_ID(N'dbfirst.Customers', N'U') IS NULL
BEGIN
    CREATE TABLE dbfirst.Customers
    (
        Id         uniqueidentifier NOT NULL CONSTRAINT DF_Customers_Id DEFAULT NEWSEQUENTIALID(),
        Name       nvarchar(150)    NOT NULL,
        Email      nvarchar(254)    NOT NULL,
        Active      bit              NOT NULL CONSTRAINT DF_Customers_Active DEFAULT 1,
        IsDeleted  bit              NOT NULL CONSTRAINT DF_Customers_IsDeleted DEFAULT 0,
        DeletedAt  datetimeoffset   NULL,
        CONSTRAINT PK_Customers PRIMARY KEY (Id)
    );

    -- Único só entre os não excluídos
    CREATE UNIQUE INDEX UX_Customers_Email ON dbfirst.Customers (Email) WHERE IsDeleted = 0;
END
GO

IF OBJECT_ID(N'dbfirst.Orders', N'U') IS NULL
BEGIN
    CREATE TABLE dbfirst.Orders
    (
        Id         bigint IDENTITY(1, 1) NOT NULL,
        CustomerId  uniqueidentifier      NOT NULL,
        Description  nvarchar(200)         NOT NULL,
        Amount      decimal(18, 2)        NOT NULL,
        CreatedOn   datetimeoffset        NOT NULL CONSTRAINT DF_Orders_CreatedOn DEFAULT SYSDATETIMEOFFSET(),
        IsDeleted  bit                   NOT NULL CONSTRAINT DF_Orders_IsDeleted DEFAULT 0,
        DeletedAt  datetimeoffset        NULL,
        CONSTRAINT PK_Orders PRIMARY KEY (Id),
        CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbfirst.Customers (Id)
    );

    CREATE INDEX IX_Orders_CustomerId ON dbfirst.Orders (CustomerId) WHERE IsDeleted = 0;
END
GO
