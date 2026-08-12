-- CaravanCMS D1 schema — ported from CaravanCMS.Core/Models.cs +
-- CaravanCMS.Api/Data/ApplicationDbContext.cs (EF Core / SQLite).
--
-- Column names preserve the original PascalCase from the EF model, to keep
-- the data migration script (scripts/migrate-db.ts) a straightforward
-- column-for-column copy from the source caravan-cms.db.
--
-- Deliberate change from the source schema: money fields move from
-- decimal-stored-as-TEXT to INTEGER cents (see plan doc — JS has no native
-- decimal type, and TEXT-string arithmetic is what the C# TEXT trick was
-- specifically working around). Convert to/from display decimal only at the
-- API boundary. Quantity gets the same treatment, as hundredths.

PRAGMA foreign_keys = ON;

CREATE TABLE Customers (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  Name TEXT NOT NULL,
  Email TEXT,
  Phone TEXT,
  Mobile TEXT,
  Address TEXT,
  Suburb TEXT,
  State TEXT,
  Postcode TEXT,
  CustomerNumber TEXT,
  MechanicDeskId TEXT,
  CreatedAt TEXT NOT NULL,
  UpdatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IX_Customers_MechanicDeskId ON Customers (MechanicDeskId) WHERE MechanicDeskId IS NOT NULL;
CREATE INDEX IX_Customers_CustomerNumber ON Customers (CustomerNumber);
CREATE INDEX IX_Customers_Name ON Customers (Name);

CREATE TABLE Caravans (
  RegistrationNumber TEXT PRIMARY KEY,
  CustomerId INTEGER NOT NULL REFERENCES Customers (Id),
  Vin TEXT,
  Make TEXT,
  Model TEXT,
  Year INTEGER,
  Color TEXT,
  Body TEXT,
  CurrentOdometer INTEGER,
  LastJobDate TEXT,
  SelfContainment TEXT,
  SelfContainmentDue TEXT,
  MechanicDeskId TEXT,
  CreatedAt TEXT NOT NULL,
  UpdatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IX_Caravans_MechanicDeskId ON Caravans (MechanicDeskId) WHERE MechanicDeskId IS NOT NULL;
CREATE INDEX IX_Caravans_Vin ON Caravans (Vin);
CREATE INDEX IX_Caravans_CustomerId ON Caravans (CustomerId);

CREATE TABLE Jobs (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  RegistrationNumber TEXT NOT NULL REFERENCES Caravans (RegistrationNumber),
  CustomerId INTEGER NOT NULL REFERENCES Customers (Id),
  JobNumber TEXT,
  Status TEXT,
  JobType TEXT,
  Description TEXT,
  Notes TEXT,
  StartDate TEXT,
  FinishDate TEXT,
  EstimatedHours REAL,
  FinishedBy TEXT,
  MechanicDeskId TEXT,
  CreatedAt TEXT NOT NULL,
  UpdatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IX_Jobs_MechanicDeskId ON Jobs (MechanicDeskId) WHERE MechanicDeskId IS NOT NULL;
CREATE INDEX IX_Jobs_JobNumber ON Jobs (JobNumber);
CREATE INDEX IX_Jobs_RegistrationNumber ON Jobs (RegistrationNumber);
CREATE INDEX IX_Jobs_Status ON Jobs (Status);

CREATE TABLE Invoices (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  JobId INTEGER NOT NULL REFERENCES Jobs (Id),
  CustomerId INTEGER NOT NULL REFERENCES Customers (Id),
  RegistrationNumber TEXT NOT NULL REFERENCES Caravans (RegistrationNumber),
  InvoiceNumber TEXT,
  IssueDate TEXT,
  DueDate TEXT,
  NetAmountCents INTEGER NOT NULL DEFAULT 0,
  TaxAmountCents INTEGER NOT NULL DEFAULT 0,
  TotalAmountCents INTEGER NOT NULL DEFAULT 0,
  PaidAmountCents INTEGER NOT NULL DEFAULT 0,
  Status TEXT,
  MechanicDeskId TEXT,
  CreatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IX_Invoices_MechanicDeskId ON Invoices (MechanicDeskId) WHERE MechanicDeskId IS NOT NULL;
CREATE INDEX IX_Invoices_InvoiceNumber ON Invoices (InvoiceNumber);
CREATE INDEX IX_Invoices_JobId ON Invoices (JobId);
CREATE INDEX IX_Invoices_Status ON Invoices (Status);

CREATE TABLE InvoiceItems (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  InvoiceId INTEGER NOT NULL REFERENCES Invoices (Id) ON DELETE CASCADE,
  Description TEXT,
  Category TEXT,
  UnitPriceCents INTEGER NOT NULL DEFAULT 0,
  QuantityHundredths INTEGER NOT NULL DEFAULT 0,
  NetAmountCents INTEGER NOT NULL DEFAULT 0,
  TaxAmountCents INTEGER NOT NULL DEFAULT 0,
  StockNumber TEXT,
  CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_InvoiceItems_InvoiceId ON InvoiceItems (InvoiceId);

CREATE TABLE Documents (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  RegistrationNumber TEXT NOT NULL REFERENCES Caravans (RegistrationNumber) ON DELETE CASCADE,
  DocumentType TEXT,
  Category TEXT,
  -- FilePath is retained as the historical local-disk path for audit purposes
  -- during migration; R2Key is the actual storage location going forward.
  FilePath TEXT NOT NULL,
  FileName TEXT NOT NULL,
  R2Key TEXT,
  UploadedDate TEXT,
  IsLocalPath INTEGER NOT NULL DEFAULT 0,
  MimeType TEXT,
  Notes TEXT,
  LinkMethod TEXT,
  CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_Documents_RegistrationNumber ON Documents (RegistrationNumber);
CREATE INDEX IX_Documents_FilePath ON Documents (FilePath);
CREATE INDEX IX_Documents_DocumentType ON Documents (DocumentType);

CREATE TABLE Conversations (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  CustomerId INTEGER NOT NULL REFERENCES Customers (Id),
  RegistrationNumber TEXT REFERENCES Caravans (RegistrationNumber),
  Subject TEXT,
  ExternalConversationId TEXT,
  StartedAt TEXT NOT NULL,
  LastActivityAt TEXT NOT NULL
);
CREATE INDEX IX_Conversations_CustomerId ON Conversations (CustomerId);
CREATE INDEX IX_Conversations_RegistrationNumber ON Conversations (RegistrationNumber);
CREATE INDEX IX_Conversations_ExternalConversationId ON Conversations (ExternalConversationId);

CREATE TABLE CommunicationLogs (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  ConversationId INTEGER NOT NULL REFERENCES Conversations (Id) ON DELETE CASCADE,
  Type TEXT NOT NULL DEFAULT 'Email',
  Direction TEXT NOT NULL DEFAULT 'Inbound',
  FromAddress TEXT,
  Body TEXT,
  ExternalMessageId TEXT,
  LoggedBy TEXT,
  OccurredAt TEXT NOT NULL,
  CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_CommunicationLogs_ConversationId ON CommunicationLogs (ConversationId);
CREATE UNIQUE INDEX IX_CommunicationLogs_ExternalMessageId ON CommunicationLogs (ExternalMessageId) WHERE ExternalMessageId IS NOT NULL;

CREATE TABLE Tags (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  CustomerId INTEGER NOT NULL REFERENCES Customers (Id) ON DELETE CASCADE,
  Name TEXT NOT NULL
);
CREATE UNIQUE INDEX IX_Tags_CustomerId_Name ON Tags (CustomerId, Name);

-- Many-to-many join table, matches EF's default naming for
-- HasMany(Conversation.Tags).WithMany(Tag.Conversations).
CREATE TABLE ConversationTags (
  ConversationsId INTEGER NOT NULL REFERENCES Conversations (Id) ON DELETE CASCADE,
  TagsId INTEGER NOT NULL REFERENCES Tags (Id) ON DELETE CASCADE,
  PRIMARY KEY (ConversationsId, TagsId)
);
