# HIS RIS Integration Service

## Overview

> Important: This project is a sanitized and simplified version of a production HIS–RIS integration middleware used for healthcare system interoperability.

The **HIS RIS Integration Service** is a Windows service that facilitates real-time integration between HIS and RIS (Radiology Information System) using HL7 messaging protocol and SQL Server Service Broker.

### Key Features

- **Real-time Integration**: Monitors database changes and automatically sends notifications
- **HL7 Protocol Support**: Communicates with RIS using industry-standard HL7 messaging
- **SQL Service Broker**: Uses SQL Server Service Broker for reliable message queuing
- **Bi-directional Communication**: Sends orders to RIS and receives reports back
- **Windows Service**: Runs as a background service with automatic startup
- **Comprehensive Logging**: Detailed logging using Serilog for troubleshooting

## Architecture

### Components

1. **HIS Database**: Source system containing patient orders and billing information
2. **SQL Service Broker**: Message queue system for reliable order transmission
3. **Integration Service**: .NET 8 Windows service that processes messages and communicates with RIS
4. **RIS**: Target radiology information system

### Data Flow

```
HIS → Service Broker Queue → Integration Service → HL7 Messages → RIS
                                                                              ↓
                                                         Reports/Status ←──────┘
```

## Database Schema

### Tables

**RISHead**
- Stores main event information
- Fields: `EventId`, `EventType`, `EventTime`, `BillId`

**RISDetail**
- Stores detailed message information
- Fields: `MessageId`, `EventId`, `OrderId`, `MessageTime`, `MessageType`, `Status`, `HL7Payload`

**RISReport**
- Stores reports received from RIS
- Fields: `ReportId`, `EventId`, `OrderId`, `ReportTime`, `ReportStatus`, `Report`, `PACS_URL`

### Service Broker Objects

- **Message Type**: `//RIS/OrderMessage`
- **Contract**: `//RIS/OrderContract`
- **Services**: 
  - `//HIS/OrderService` (Initiator)
  - `//RIS/OrderService` (Target)
- **Queues**: 
  - `HISOrderQueue`
  - `RISOrderQueue`

## Installation

### Prerequisites

- Windows Server or Windows 10/11
- .NET 8 Runtime ([Download](https://dotnet.microsoft.com/download/dotnet/8.0)) - *Not required if the project is published using `--self-contained true`, as the runtime will be bundled with the application (recommended)*.
- SQL Server (2016 or later recommended)
- Administrative privileges
- Network connectivity to RIS

### Step 1: Database Setup

Run the provided SQL script to set up Service Broker:

<span style="color:red">
<b>Note:</b> <i>Enabling Service Broker on a SQL Server database requires a maintenance window, as the database must be switched to SINGLE_USER mode.</i>
</span>

```sql
-- Execute the SQL scripts one by one
-- This will:
-- 1. Enable Service Broker on HIS database
-- 2. Create message types and contracts
-- 3. Create queues and services
-- 4. Create necessary tables
-- 5. Create stored procedure sp_NotifyRISMiddleware

-- =======================================================================
-- 1. Enable Service Broker on HIS database
-- =======================================================================

USE master;

-- Check active connections
SELECT COUNT(*) AS ActiveConnections
FROM sys.dm_exec_sessions
WHERE database_id = DB_ID('HIS')
AND session_id != @@SPID;

-- Change DB mode to SINGLE_USER 
ALTER DATABASE HIS SET SINGLE_USER WITH ROLLBACK AFTER 60 SECONDS;

-- View DB user mode
SELECT name, user_access_desc 
FROM sys.databases 
WHERE name = 'HIS';

-- Enable Service Broker on database
ALTER DATABASE HIS SET ENABLE_BROKER;

-- Restore DB mode to MULTI_USER
ALTER DATABASE HIS SET MULTI_USER;
-- =======================================================================
-- 2. Create message types and contracts
-- =======================================================================
USE HIS;

-- Create Message Type (NONE = no validation)
CREATE MESSAGE TYPE [//RIS/OrderMessage]
    VALIDATION = NONE;

-- Create Contract
CREATE CONTRACT [//RIS/OrderContract]
    ([//RIS/OrderMessage] SENT BY INITIATOR);

-- =======================================================================
-- 3. Create queues and services
-- =======================================================================
-- HIS
CREATE QUEUE HISOrderQueue;

CREATE SERVICE [//HIS/OrderService]
    ON QUEUE HISOrderQueue
    ([//RIS/OrderContract]);

-- RIS
CREATE QUEUE RISOrderQueue;

CREATE SERVICE [//RIS/OrderService]
    ON QUEUE RISOrderQueue
    ([//RIS/OrderContract]);
    
-- View created services
SELECT
    s.name          AS service_name,
    q.name          AS queue_name,
    s.service_id
FROM sys.services s
JOIN sys.service_queues q
    ON s.service_queue_id = q.object_id
ORDER BY s.name;

-- =======================================================================
-- 4. Create necessary tables
-- =======================================================================

IF OBJECT_ID ('dbo.RISHead') IS NOT NULL
	DROP TABLE dbo.RISHead
GO

CREATE TABLE dbo.RISHead
	(
	EventId   BIGINT IDENTITY NOT NULL,
	EventType NVARCHAR (50) NULL,
	EventTime DATETIME NULL,
	BillId    BIGINT NULL
	)
GO

IF OBJECT_ID ('dbo.RISDetail') IS NOT NULL
	DROP TABLE dbo.RISDetail
GO

CREATE TABLE dbo.RISDetail
	(
	MessageId   BIGINT IDENTITY NOT NULL,
	EventId     BIGINT NULL,
	OrderId     BIGINT NULL,
	MessageTime DATETIME NULL,
	MessageType NVARCHAR (50) NULL,
	Status      NVARCHAR (50) NULL,
	HL7Payload  NVARCHAR (2047) NULL,
	CONSTRAINT PK_RISDetail PRIMARY KEY (MessageId)
	)
GO

IF OBJECT_ID ('dbo.RISReport') IS NOT NULL
	DROP TABLE dbo.RISReport
GO

CREATE TABLE dbo.RISReport
	(
	ReportId     BIGINT IDENTITY NOT NULL,
	EventId      BIGINT NULL,
	OrderId      BIGINT NULL,
	ReportTime   DATETIME NULL,
	ReportStatus NVARCHAR (10) NULL,
	Report       NVARCHAR (4000) NULL,
	PACS_URL     NVARCHAR (500) NULL,
	CONSTRAINT PK_RISReport PRIMARY KEY (ReportId)
	)
GO

CREATE TABLE dbo.RISMap
	(
	MapId      BIGINT IDENTITY NOT NULL,
	ServiceCat VARCHAR (50) NOT NULL,
	AE_Title   VARCHAR (50) NULL,
	Modality   VARCHAR (50) NULL,
	IsActive   BIT CONSTRAINT DF_RISMap_IsActive DEFAULT ((1)) NOT NULL,
	CONSTRAINT PK_RISMap PRIMARY KEY (MapId)
	)
GO


-- =======================================================================
-- 5. Create sp that will send message to the service broker
-- =======================================================================

CREATE PROCEDURE [dbo].[sp_NotifyRISMiddleware]
    @BillId INT
AS 
BEGIN
    DECLARE @ConversationHandle UNIQUEIDENTIFIER;

    BEGIN DIALOG CONVERSATION @ConversationHandle
        FROM SERVICE [//HISRIS/OrderService]
        TO SERVICE '//RIS/OrderService'
        ON CONTRACT [//RIS/OrderContract]
        WITH ENCRYPTION = OFF;

    SEND ON CONVERSATION @ConversationHandle
        MESSAGE TYPE [//RIS/OrderMessage]
        (CAST(@BillId AS NVARCHAR(50)));

    END CONVERSATION @ConversationHandle;
END;
```

### Step 2: Build/Publish Application

Publish the application to your installation directory:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

Publish will be in:
```
bin\Release\net8.0\win-x64\publish
```

### Step 3: Install Windows Service

Open PowerShell as Administrator and run:

```powershell
New-Service `
  -Name "HISRISIntegration" `
  -BinaryPathName "D:\HISRISIntegration\HIS_RIS_Integration.exe" `
  -DisplayName "HIS RIS Integration Service" `
  -Description "Integration service between HIS and RIS via HL7 and SQL Broker" `
  -StartupType Automatic
```

**Important**: Update the `-BinaryPathName` to match your actual installation path.

### Step 4: Start the Service

```powershell
Start-Service HISRISIntegration
```

### Step 5: Verify Installation

Check service status:

```powershell
Get-Service HISRISIntegration
```

Expected output:
```
Status   Name                               DisplayName
------   ----                               -----------
Running  HISRISIntegration     HIS RIS Inte...
```

Check logs at `D:\HISRISIntegration\logs` for "Integration Service Started" message.

## Configuration

The service is configured through `appsettings.json` located in the installation directory.

### Sample Configuration Structure

```json
{
  "Database": {
    "ConnectionString": "[Encrypted connection string]"
  },
  "RIS": {
    "Host": "192.168.1.100",
    "Port": 2575
  },
  "HIS": {
    "Port": 2576
  },
  "Serilog": {
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "logs/service-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 30
        }
      }
    ]
  }
}
```

### Configuration Settings

**Database**
- `ConnectionString`: Encrypted SQL Server connection string

**RIS**
- `Host`: IP address or hostname of RIS server
- `Port`: Port number for HL7 communication (typically 2575)

**HIS**
- `Port`: Listener port for receiving RIS responses (typically 2576)

**Serilog**
- Log file path and retention settings
- Default: 30 days retention, daily rolling files

## Usage

### Triggering an Order

HIS will execute the stored procedure with a Bill ID after the bill is created or deleted:

```sql
EXEC sp_NotifyRISMiddleware @BillId = 154011 -- Any valid bill no.
```

This will:
1. Create a Service Broker conversation
2. Send the Bill ID to the queue
3. Integration service picks up the message
4. Checks if it is a new bill or a deleted bill
5. Generate and sends HL7 message to RIS

### Monitoring Messages

Check messages in queue:

```sql
SELECT * FROM RISOrderQueue;
```

Receive a message manually (for testing):

```sql
WAITFOR (
    RECEIVE TOP (1)
        conversation_handle,
        message_type_name,
        message_body
    FROM RISOrderQueue
), TIMEOUT 60000;
```

### View Integration History

Check sent messages:
```sql
SELECT * FROM RISDetail ORDER BY MessageTime DESC;
```

Check received reports:
```sql
SELECT * FROM RISReport ORDER BY ReportTime DESC;
```

## Troubleshooting

### Service Won't Start

1. Check Windows Event Viewer for error details
2. Verify .NET 8 Runtime is installed
3. Ensure executable path is correct
4. Check file permissions on installation directory

### Messages Not Being Sent

1. Verify Service Broker is enabled:
   ```sql
   SELECT is_broker_enabled FROM sys.databases WHERE name = 'HIS';
   ```
2. Check queue for stuck messages
3. Review service logs for error messages
4. Verify network connectivity to RIS server

### Log Locations

- **Service Logs**: `D:\HISRISIntegration\logs\service-YYYYMMDD.log`
- **Windows Event Logs**: Event Viewer → Windows Logs → Application

## Uninstallation

### Stop the Service

```powershell
Stop-Service HISRISIntegration
```

### Delete the Service

```powershell
sc.exe delete HISRISIntegration
```

Expected output:
```
[SC] DeleteService SUCCESS
```

### Verify Removal

```powershell
Get-Service HISRISIntegration
```

Expected output:
```
Cannot find any service with service name 'HISRISIntegration'
```

### Optional: Clean Up Database Objects

If you want to remove all Service Broker objects:

```sql
-- Drop services
DROP SERVICE [//RIS/OrderService];
DROP SERVICE [//HIS/OrderService];

-- Drop queues
DROP QUEUE RISOrderQueue;
DROP QUEUE HISOrderQueue;

-- Drop contract and message type
DROP CONTRACT [//RIS/OrderContract];
DROP MESSAGE TYPE [//RIS/OrderMessage];
```

## Maintenance

### Log Rotation

Logs automatically rotate daily. Retention is configurable in `appsettings.json` (default: 30 days).

### Service Updates

1. Stop the service
2. Replace executable and configuration files
3. Restart the service
4. Monitor logs for successful startup

## Version Information

- **.NET Version**: 8.0
- **Protocol**: HL7
- **Database**: SQL Server 2016+
- **Platform**: Windows

---

**Note**: This documentation assumes the service executable is named `HIS_RIS_Integration.exe`.
