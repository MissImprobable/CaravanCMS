/** Raw D1 row shapes — column names match migrations/0001_init.sql exactly. */

export interface CustomerRow {
  Id: number;
  Name: string;
  Email: string | null;
  Phone: string | null;
  Mobile: string | null;
  Address: string | null;
  Suburb: string | null;
  State: string | null;
  Postcode: string | null;
  CustomerNumber: string | null;
  MechanicDeskId: string | null;
  CreatedAt: string;
  UpdatedAt: string;
}

export interface CaravanRow {
  RegistrationNumber: string;
  CustomerId: number;
  Vin: string | null;
  Make: string | null;
  Model: string | null;
  Year: number | null;
  Color: string | null;
  Body: string | null;
  CurrentOdometer: number | null;
  LastJobDate: string | null;
  SelfContainment: string | null;
  SelfContainmentDue: string | null;
  SelfContainmentIssueDate: string | null;
  WofIssueDate: string | null;
  WofDueDate: string | null;
  ElectricalWofIssueDate: string | null;
  ElectricalWofDueDate: string | null;
  LockerKeyNumber: string | null;
  DoorKeyNumber: string | null;
  Notes: string | null;
  MechanicDeskId: string | null;
  CreatedAt: string;
  UpdatedAt: string;
}

export interface JobRow {
  Id: number;
  RegistrationNumber: string;
  CustomerId: number;
  JobNumber: string | null;
  Status: string | null;
  JobType: string | null;
  Description: string | null;
  Notes: string | null;
  StartDate: string | null;
  FinishDate: string | null;
  EstimatedHours: number | null;
  FinishedBy: string | null;
  MechanicDeskId: string | null;
  CreatedAt: string;
  UpdatedAt: string;
}

export interface InvoiceRow {
  Id: number;
  JobId: number;
  CustomerId: number;
  RegistrationNumber: string;
  InvoiceNumber: string | null;
  IssueDate: string | null;
  DueDate: string | null;
  NetAmountCents: number;
  TaxAmountCents: number;
  TotalAmountCents: number;
  PaidAmountCents: number;
  Status: string | null;
  MechanicDeskId: string | null;
  CreatedAt: string;
}

export interface InvoiceItemRow {
  Id: number;
  InvoiceId: number;
  Description: string | null;
  Category: string | null;
  UnitPriceCents: number;
  QuantityHundredths: number;
  NetAmountCents: number;
  TaxAmountCents: number;
  StockNumber: string | null;
  CreatedAt: string;
}

export interface DocumentRow {
  Id: number;
  RegistrationNumber: string;
  DocumentType: string | null;
  Category: string | null;
  FilePath: string;
  FileName: string;
  R2Key: string | null;
  UploadedDate: string | null;
  IsLocalPath: number;
  MimeType: string | null;
  Notes: string | null;
  LinkMethod: string | null;
  CreatedAt: string;
}

export interface ConversationRow {
  Id: number;
  CustomerId: number;
  RegistrationNumber: string | null;
  Subject: string | null;
  ExternalConversationId: string | null;
  StartedAt: string;
  LastActivityAt: string;
}

export interface CommunicationLogRow {
  Id: number;
  ConversationId: number;
  Type: string;
  Direction: string;
  FromAddress: string | null;
  Body: string | null;
  ExternalMessageId: string | null;
  LoggedBy: string | null;
  OccurredAt: string;
  CreatedAt: string;
}

export interface TagRow {
  Id: number;
  CustomerId: number;
  Name: string;
}
