using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeAgentPipelines.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Meta",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Meta", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Pipelines",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipelines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WorkspacePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NextNumber = table.Column<int>(type: "int", nullable: false),
                    Azure_SubscriptionId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Azure_SubscriptionName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Azure_ResourceGroup = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Azure_Location = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Azure_DeployEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Autopilot = table.Column<bool>(type: "bit", nullable: false),
                    MaxParallelFeatures = table.Column<int>(type: "int", nullable: false),
                    Orchestration_Planning = table.Column<bool>(type: "bit", nullable: false),
                    Orchestration_RunId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Orchestration_PlannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Orchestration_Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Orchestration_Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PipelineId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PipelineName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProjectId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FeatureId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Input = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PipelineId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Features",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProjectId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DependsOn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OrchestratorNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PlannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CurrentRunId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RunIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Branch = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WorktreePath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PlanPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeploymentUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Features", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Features_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamAgents",
                columns: table => new
                {
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProjectId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Instructions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamAgents", x => new { x.ProjectId, x.Role });
                    table.ForeignKey(
                        name: "FK_TeamAgents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Steps",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RunId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Activity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Output = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Steps", x => new { x.RunId, x.AgentId });
                    table.ForeignKey(
                        name: "FK_Steps_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentFiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AgentDefinitionId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentFiles_Agents_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatureTasks",
                columns: table => new
                {
                    StepId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FeatureId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureTasks", x => x.StepId);
                    table.ForeignKey(
                        name: "FK_FeatureTasks_Features_FeatureId",
                        column: x => x.FeatureId,
                        principalTable: "Features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentFiles_AgentDefinitionId",
                table: "AgentFiles",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_PipelineId",
                table: "Agents",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_Features_ProjectId_Number",
                table: "Features",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureTasks_FeatureId",
                table: "FeatureTasks",
                column: "FeatureId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_CompletedAt",
                table: "Runs",
                column: "CompletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentFiles");

            migrationBuilder.DropTable(
                name: "FeatureTasks");

            migrationBuilder.DropTable(
                name: "Meta");

            migrationBuilder.DropTable(
                name: "Steps");

            migrationBuilder.DropTable(
                name: "TeamAgents");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "Features");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Pipelines");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
