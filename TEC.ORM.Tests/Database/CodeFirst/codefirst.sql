IF OBJECT_ID(N'[codefirst].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'codefirst') IS NULL EXEC(N'CREATE SCHEMA [codefirst];');
    CREATE TABLE [codefirst].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    IF SCHEMA_ID(N'codefirst') IS NULL EXEC(N'CREATE SCHEMA [codefirst];');
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    CREATE TABLE [codefirst].[Categories] (
        [Id] nvarchar(20) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
        [DeletedAt] datetimeoffset NULL,
        CONSTRAINT [PK_Categories] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    CREATE TABLE [codefirst].[Customers] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(150) NOT NULL,
        [Email] nvarchar(254) NOT NULL,
        [Active] bit NOT NULL,
        [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
        [DeletedAt] datetimeoffset NULL,
        CONSTRAINT [PK_Customers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    CREATE TABLE [codefirst].[Products] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(150) NOT NULL,
        [Price] decimal(18,2) NOT NULL,
        [CategoryId] nvarchar(20) NOT NULL,
        [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
        [DeletedAt] datetimeoffset NULL,
        CONSTRAINT [PK_Products] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Products_Categories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [codefirst].[Categories] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    CREATE TABLE [codefirst].[Orders] (
        [Id] bigint NOT NULL IDENTITY,
        [CustomerId] uniqueidentifier NOT NULL,
        [ProductId] int NOT NULL,
        [Quantity] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [CreatedOn] datetimeoffset NOT NULL,
        [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
        [DeletedAt] datetimeoffset NULL,
        CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Orders_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [codefirst].[Customers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Orders_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [codefirst].[Products] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Categories_IsDeleted] ON [codefirst].[Categories] ([IsDeleted]) WHERE [IsDeleted] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Customers_Email] ON [codefirst].[Customers] ([Email]) WHERE [IsDeleted] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Customers_IsDeleted] ON [codefirst].[Customers] ([IsDeleted]) WHERE [IsDeleted] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    CREATE INDEX [IX_Orders_CustomerId] ON [codefirst].[Orders] ([CustomerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Orders_IsDeleted] ON [codefirst].[Orders] ([IsDeleted]) WHERE [IsDeleted] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    CREATE INDEX [IX_Orders_ProductId] ON [codefirst].[Orders] ([ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    CREATE INDEX [IX_Products_CategoryId] ON [codefirst].[Products] ([CategoryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Products_IsDeleted] ON [codefirst].[Products] ([IsDeleted]) WHERE [IsDeleted] = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [codefirst].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005132331_InicialDbTecBase'
)
BEGIN
    INSERT INTO [codefirst].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261005132331_InicialDbTecBase', N'10.0.12');
END;

COMMIT;
GO

