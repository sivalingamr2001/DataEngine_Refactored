Database Configuration Summary: The DatabaseConfig class defines the connectivity parameters for the application's data persistence layer. 
								It encapsulates the core credentials (ConnectionString), identifies the targeted SQL dialect wrapper (Provider), 
								and establishes a temporal baseline (DefaultTimezone) to ensure uniform date-time offsets across database transactions.
								
								ConnectionString: The foundational credential string containing the server address, database name, user authentication, and security parameters needed to establish a connection.
								Provider: An engine type flag dictated by the DatabaseProvider enum (PostgreSQL, SqlServer, MySQL) that determines which specific SQL syntax and driver rules apply during query compilation.
								DefaultTimezone: The fallback fallback timezone string (defaults to "UTC") applied to database operations when a specific timezone is not requested by the user config.
								
Global Cache Options Summary: The cache configuration properties establish application-wide caching behavior, driving high-performance data recycling. 
							  It provides architectural flexibility by toggling between localized memory buffers and standalone distributed infrastructure, complete with connection routing data and standard time-to-live policies.
							  
							  CacheProvider: An infrastructure toggle dictated by the CacheProvider enum that routes data to localized hardware infrastructure (InMemory) or an external decoupled cache layer (Redis).
							  RedisConnectionString: The network address, port, access keys, and timeout details necessary to route traffic and authenticates requests to a remote distributed Redis cluster.
							  CacheExpirationMinutes: The default sliding or absolute time-to-live threshold (defaults to 30 minutes) before a cached payload is considered stale and discarded.
							  
Portal Configuration Summary: Every DB have lots of tables but each project consume n number of tables only so this services needs a Application tables as meta data or master table add it here to use.

Field Mapper Service Summary: The FieldMapperService class provides a dynamic metadata engine and schema-mapping layer for the application. 
							  It decouples core business logic from database schemas by resolving table layouts, dynamic foreign key relations, and display configurations at runtime. 
							  The service leverages optimized multi-layered caching strategies and abstract connection multiplexing to deliver high-performance metadata translation across isolated data scopes and active transaction pipelines.
							  
							  FieldMapperCache: The specialized caching layer instance (IFieldMapperCache) used to store, retrieve, and recycle resolved table configurations and database mappings to prevent redundant database roundtrips.
							  Logger: The application logging provider instance (ILogger) used to record runtime operational statistics, execution tracking metrics, and detailed diagnostic traces when processing data operations.
							  ConnectionFactory: The foundational factory engine instance (IDbConnectionFactory) responsible for instantiating and opening active vendor-specific database connection wrappers based on localized environments.
							  
Core Entity DefinitionsDynamic Field Mapper Summary: The FieldMapper class defines the metadata blueprint for an individual column or entity field inside the system. 
													 It dictates how raw table data maps to application definitions, resolves underlying relational lookups, 
													 and tracks structural datatype configurations required for dynamic forms and data grid renders.
													 
													 Id: The unique systemic integer identifier assigned to a specific field mapping definition entry within the configuration datastore.
													 TableName: The physical name of the target database table or view being mapped (stored under entityname in the storage schema).
													 FieldName: The explicit column name inside the database table that requires mapping or resolution rules.
													 LookupTable: The foreign target database table name used to fetch human-readable text when the field acts as a relational key pointer.
													 LookupIdColumn: The primary or unique key column name inside the foreign lookup table used for relational structural matching (defaults to "id").
													 LookupIdColumnDataType: The explicit storage type definition (e.g., int, uuid, bigint) of the foreign target key column used to enforce type-safe data conversion.
													 LabelColumn: The specific text-based column name inside the foreign lookup table that contains the friendly, human-readable display value.
													 GroupOptionKey: The optional master category structural pointer used to tie the field to standardized choice collection elements if it does not rely on a standalone lookup table.
													 
Dynamic Relational MappingsReference Label Summary: The ReferenceLabel class encapsulates a resolved translation pair designed for user interface rendering. 
													It pairs an underlying primitive data key with its human-readable description, allowing grids, exports, 
													and profile screens to transparently substitute complex relational IDs with clear descriptive titles.
													
													Id: The raw identifier key value retrieved from the database, boxed as an object to seamlessly support integer, bigint, and string/UUID primary keys.
													Label: The human-readable description string mapped to the ID, automatically fallback-initialized to an empty string if the database column contains a null record.

Fetch Query:

	Scope: This asynchronous method handles data retrieval, caching, query building, security validation, and pagination for database operations. 
		   It manages various query types: direct raw SQL, standard repository-stored queries. 
		   The method controls the lifecycle of a shared database connection, executes server-side filtering, and enforces strict security checks. 
		   It relies on external dependencies like _cacheService, _connectionFactory, _sqlValidator, _queryRepository, and _logger to perform its tasks.
		   
	Input: The FetchConfig class is a comprehensive configuration object that controls how a database query is fetched, secured, transformed, and returned. 
		   It acts as a single control panel for the execution engine, dictating whether to use pre-defined or direct SQL, managing performance features like caching, 
		   and handling UI-driven data shaping such as pagination, sorting, filtering, and timezone adjustments.
		   
		   QueryNumber: Unique identifier used to look up a pre-configured, standard SQL query definition from the system repository.
		   InputParameters: A dynamic JSON object storing key-value pairs used to safely bind parameters and arguments into the target query.
		   Count: Dictates the maximum number of rows or records to return in a single page (defaults to 10).
		   PageNumber: Specifies the current page index for data retrieval, used to calculate pagination offsets (defaults to 1).
		   IncludeReferenceLabels: Boolean flag indicating whether human-readable lookup text or labels should be mapped and returned alongside raw database IDs.
		   EnableServerSideSorting: Toggle that activates sorting logic directly inside the database query engine rather than in memory.
		   SortField: The database column name or property used to order the final query results.
		   SortDirection: Determines the sorting order sequence, explicitly accepting either "asc" (ascending) or "desc" (descending).
		   EnableServerSideFiltering: Toggle that triggers the execution engine to dynamically inject explicit WHERE clauses into the query.
		   FilterConditions: A collection of structured criteria objects representing specific columns, operators, and values to filter the data.
		   FetchTimezone: Target timezone identifier string used to normalize and convert datetime columns before returning results to the client.
		   SearchText: A global search string used for fuzzy matching or full-text scanning across searchable columns.
		   QueryText: Raw SQL statement passed directly from the client when bypass operations are required.
		   EnableDirectQueryExecution: Safety flag allowing the execution of raw QueryText instead of repository-stored queries (defaults to true).
		   EnableCaching: Performance toggle determining if the execution engine should read from or write to the system cache middleware.

Tables: 
-- Create Applications TABLE
TODO: Need to build column for this table

-- Create field_mappers table for reference data lookup
CREATE TABLE IF NOT EXISTS field_mappers (
    id SERIAL PRIMARY KEY,
    table_name VARCHAR(100) NOT NULL,
    field_name VARCHAR(100) NOT NULL,
    lookup_table VARCHAR(100),
    lookup_id_column VARCHAR(100) DEFAULT 'id',
    lookup_id_column_datatype VARCHAR(50) DEFAULT 'int',  -- int, guid, string, bigint
    label_column VARCHAR(100),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(table_name, field_name)
);

-- de_query_definitions: saved named queries
CREATE TABLE de_query_definitions (
    id              INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    definition_key  VARCHAR(128) NOT NULL UNIQUE,
    table_name      VARCHAR(128) NOT NULL,
    description     TEXT,
    query_text      JSON NOT NULL,  -- serialized QueryRequest (minus table)
    is_active       TINYINT(1) NOT NULL DEFAULT 1,
    created_at      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- de_transaction_audit: audit trail
CREATE TABLE de_transaction_audit (
    id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    operation     ENUM('INSERT','UPDATE','DELETE') NOT NULL,
    table_name    VARCHAR(128) NOT NULL,
    database_name VARCHAR(128) NOT NULL,
    record_id     VARCHAR(255),
    old_values    JSON,
    new_values    JSON,
    executed_by   VARCHAR(255),
    executed_at   DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    correlation_id VARCHAR(64),
    INDEX idx_table_op (table_name, operation),
    INDEX idx_executed_at (executed_at),
    INDEX idx_correlation (correlation_id)
);

Based on the comprehensive configuration, metadata structures, and database schemas provided, this system is a Dynamic Meta-Driven Data Access Layer (DAL). 
It operates as a generic query execution engine that eliminates the need to write repetitive CRUD or search endpoints by relying on database-driven mappings.

Here is the short analysis of its pros and cons:

Service Pros

Rapid Development: Developers do not need to build new APIs or endpoints for new tables; adding data models is completely configuration-driven.
Loose Coupling: Business logic is separated from the database structure, allowing runtime table modifications without recompiling code.
Automatic UI Enrichment: The system automates user interface rendering by automatically substituting raw relational keys with human-readable descriptions (ReferenceLabel).
High Performance: It features an abstract, multi-layered caching layout (InMemory/Redis) to eliminate redundant mapping lookups and database roundtrips.
Built-in Governance: Includes robust, system-level tracking out of the box via structured JSON audit trails (de_transaction_audit) and explicit validation layers.

Service Cons

Security Risks: Accepting client-side raw SQL (QueryText) introduces severe vulnerabilities to SQL injection attacks if the _sqlValidator component fails.
Performance Bottlenecks: Generating dynamic SQL queries with automated filtering, sorting, and text-matching can yield unoptimized execution plans and degrade performance on larger tables.
Single Point of Failure: Because all entity workflows route through this single generic service, a bug or failure within this layer impacts every application in the ecosystem.
Complex Debugging: Isolating issues, tracing errors, and profiling slow database connections becomes challenging when queries are composed at runtime from complex JSON objects.
Vendor Lock-in Potential: The structure mixes engine configurations (PostgreSQL, SqlServer, MySQL), which complicates maintaining clean, cross-platform compatibility for dynamic, vendor-specific syntax wrappers.

Fix this cons and more enhanced data security aviod sql injections more secureable service.