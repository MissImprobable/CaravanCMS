-- Fields never populated by MechanicDesk import — only ever entered manually by staff via the
-- Client app's Vehicle Info tab (Make/Model/Year/Vin edit support added alongside these; see
-- PATCH /api/caravans/:rego). Existing SelfContainment/SelfContainmentDue columns kept as-is;
-- SelfContainmentIssueDate added to match the issue+due pattern used for the two new WOF fields.
ALTER TABLE Caravans ADD COLUMN WofIssueDate TEXT;
ALTER TABLE Caravans ADD COLUMN WofDueDate TEXT;
ALTER TABLE Caravans ADD COLUMN ElectricalWofIssueDate TEXT;
ALTER TABLE Caravans ADD COLUMN ElectricalWofDueDate TEXT;
ALTER TABLE Caravans ADD COLUMN SelfContainmentIssueDate TEXT;
ALTER TABLE Caravans ADD COLUMN LockerKeyNumber TEXT;
ALTER TABLE Caravans ADD COLUMN DoorKeyNumber TEXT;
ALTER TABLE Caravans ADD COLUMN Notes TEXT;
