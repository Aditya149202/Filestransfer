protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTable(
        name: "ProcessedEvents",
        columns: table => new
        {
            OrderId = table.Column<int>(type: "int", nullable: false),
            ProcessedAt = table.Column<DateTime>(
                type: "datetime2",
                nullable: false,
                defaultValueSql: "GETUTCDATE()")
        },
        constraints: table =>
        {
            table.PrimaryKey("PK_ProcessedEvents", x => x.OrderId);
        });
}
