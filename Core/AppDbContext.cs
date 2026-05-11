using System;
using System.IO;
using BearingFaultDiagnosis.Entities;
using Microsoft.EntityFrameworkCore;

namespace BearingFaultDiagnosis.Core
{
    public class AppDbContext : DbContext
    {
        private string strDb = $"Data Source = {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SQLite\\sqlite.db")}";

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured && !string.IsNullOrWhiteSpace(strDb))
            {
                // 提取并确保目录存在
                var dbPath = strDb.Replace("Data Source = ", "").Trim();
                var directory = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                optionsBuilder.UseSqlite(strDb);
            }
        }
        public DbSet<User> Users => Set<User>();

        public DbSet<Role> Roles => Set<Role>();

        public DbSet<Menu> Menus => Set<Menu>();

        public DbSet<RoleMenu> RoleMenus => Set<RoleMenu>();

        public DbSet<DeviceInfo> DeviceInfos => Set<DeviceInfo>();

        public DbSet<DeviceCommandLog> DeviceCommandLogs => Set<DeviceCommandLog>();

        public DbSet<BearingInfo> BearingInfos => Set<BearingInfo>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Role>(entity =>
            {
                entity.HasIndex(x => x.Code).IsUnique();
                entity.HasIndex(x => x.Name).IsUnique();
            });

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(x => x.UserName).IsUnique();
                entity.HasOne(x => x.Role)
                    .WithMany(x => x.Users)
                    .HasForeignKey(x => x.RoleId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<Menu>(entity =>
            {
                entity.HasIndex(x => x.Route).IsUnique();
                entity.HasOne(x => x.Parent)
                    .WithMany(x => x.Children)
                    .HasForeignKey(x => x.ParentId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<RoleMenu>(entity =>
            {
                entity.HasKey(x => new { x.RoleId, x.MenuId });
                entity.HasOne(x => x.Role)
                    .WithMany(x => x.RoleMenus)
                    .HasForeignKey(x => x.RoleId);
                entity.HasOne(x => x.Menu)
                    .WithMany(x => x.RoleMenus)
                    .HasForeignKey(x => x.MenuId);
            });

            modelBuilder.Entity<DeviceInfo>(entity =>
            {
                entity.HasIndex(x => x.DeviceCode).IsUnique();
            });

            modelBuilder.Entity<DeviceCommandLog>(entity =>
            {
                entity.HasOne(x => x.DeviceInfo)
                    .WithMany(x => x.CommandLogs)
                    .HasForeignKey(x => x.DeviceInfoId);
            });

            modelBuilder.Entity<BearingInfo>(entity =>
            {
                entity.HasIndex(x => new { x.Manufacturer, x.Model }).IsUnique();
            });

            var seedTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            modelBuilder.Entity<Role>().HasData(
                new Role { Id = 1, Name = "管理员", Code = "ADMIN", Description = "系统管理员", IsEnabled = true },
                new Role { Id = 2, Name = "普通用户", Code = "USER", Description = "普通操作用户", IsEnabled = true }
            );
            string defaultPasswordHash = BCrypt.Net.BCrypt.HashPassword("123456");
            modelBuilder.Entity<User>().HasData(
                new User
                {
                    Id = 1,
                    UserName = "admin",
                    PasswordHash = defaultPasswordHash,
                    DisplayName = "系统管理员",
                    IsActive = true,
                    CreatedAt = seedTime,
                    RoleId = 1
                },
                new User
                {
                    Id = 2,
                    UserName = "user",
                    PasswordHash = defaultPasswordHash,
                    DisplayName = "普通用户",
                    IsActive = true,
                    CreatedAt = seedTime,
                    RoleId = 2
                }
            );

            modelBuilder.Entity<Menu>().HasData(
                new Menu { Id = 1, Name = "实时监控看板", Route = "dashboard", Icon = "Monitor", SortOrder = 1, IsVisible = true },
                new Menu { Id = 2, Name = "深度诊断", Route = "deep-diagnosis", Icon = "Stethoscope", SortOrder = 2, IsVisible = true },
                new Menu { Id = 3, Name = "故障诊断报告", Route = "diagnosis-report", Icon = "FileText", SortOrder = 3, IsVisible = true },
                new Menu { Id = 4, Name = "系统设置参数", Route = "system-settings", Icon = "Settings", SortOrder = 4, IsVisible = true },
                new Menu { Id = 5, Name = "用户", Route = "users", Icon = "Users", SortOrder = 5, IsVisible = true },
                new Menu { Id = 6, Name = "角色", Route = "roles", Icon = "Shield", SortOrder = 6, IsVisible = true },
                new Menu { Id = 7, Name = "菜单", Route = "menus", Icon = "Menu", SortOrder = 7, IsVisible = true },
                new Menu { Id = 8, Name = "设备管理", Route = "devices", Icon = "Cpu", SortOrder = 8, IsVisible = true }
            );

            modelBuilder.Entity<RoleMenu>().HasData(
                new RoleMenu { RoleId = 2, MenuId = 1, GrantedAt = seedTime },
                new RoleMenu { RoleId = 2, MenuId = 2, GrantedAt = seedTime },
                new RoleMenu { RoleId = 2, MenuId = 3, GrantedAt = seedTime },
                new RoleMenu { RoleId = 2, MenuId = 4, GrantedAt = seedTime },
                new RoleMenu { RoleId = 1, MenuId = 1, GrantedAt = seedTime },
                new RoleMenu { RoleId = 1, MenuId = 2, GrantedAt = seedTime },
                new RoleMenu { RoleId = 1, MenuId = 3, GrantedAt = seedTime },
                new RoleMenu { RoleId = 1, MenuId = 4, GrantedAt = seedTime },
                new RoleMenu { RoleId = 1, MenuId = 5, GrantedAt = seedTime },
                new RoleMenu { RoleId = 1, MenuId = 6, GrantedAt = seedTime },
                new RoleMenu { RoleId = 1, MenuId = 7, GrantedAt = seedTime },
                new RoleMenu { RoleId = 1, MenuId = 8, GrantedAt = seedTime }
            );

            modelBuilder.Entity<DeviceInfo>().HasData(
                new DeviceInfo
                {
                    Id = 1,
                    DeviceCode = "DEV-001",
                    DeviceName = "下位机 1",
                    IpAddress = "192.168.1.100",
                    Port = 5000,
                    Protocol = "TCP",
                    FirmwareVersion = "1.0.0",
                    InstallLocation = "产线 A",
                    IsOnline = false,
                    LastSeenAt = null,
                    Remark = "默认设备"
                }
            );

            modelBuilder.Entity<BearingInfo>().HasData(
                new BearingInfo { Id = 1, Manufacturer = "SKF", Model = "6205", RollerCount_n = 9, RollerDiameter_d = 7.94, PitchDiameter_D = 39.04, ContactAngle_alpha = 0, BPFO_Multiplier = 3.585, BPFI_Multiplier = 5.415, BSF_Multiplier = 2.311, FTF_Multiplier = 0.398 },
                new BearingInfo { Id = 2, Manufacturer = "SKF", Model = "6206", RollerCount_n = 9, RollerDiameter_d = 9.53, PitchDiameter_D = 46.36, ContactAngle_alpha = 0, BPFO_Multiplier = 3.610, BPFI_Multiplier = 5.390, BSF_Multiplier = 2.319, FTF_Multiplier = 0.401 },
                new BearingInfo { Id = 3, Manufacturer = "SKF", Model = "6305", RollerCount_n = 8, RollerDiameter_d = 10.32, PitchDiameter_D = 44.60, ContactAngle_alpha = 0, BPFO_Multiplier = 3.128, BPFI_Multiplier = 4.872, BSF_Multiplier = 2.109, FTF_Multiplier = 0.391 },
                new BearingInfo { Id = 4, Manufacturer = "SKF", Model = "6311", RollerCount_n = 8, RollerDiameter_d = 14.29, PitchDiameter_D = 71.50, ContactAngle_alpha = 0, BPFO_Multiplier = 3.129, BPFI_Multiplier = 4.871, BSF_Multiplier = 2.133, FTF_Multiplier = 0.391 },
                new BearingInfo { Id = 5, Manufacturer = "NSK", Model = "6205", RollerCount_n = 9, RollerDiameter_d = 7.94, PitchDiameter_D = 39.04, ContactAngle_alpha = 0, BPFO_Multiplier = 3.585, BPFI_Multiplier = 5.415, BSF_Multiplier = 2.311, FTF_Multiplier = 0.398 },
                new BearingInfo { Id = 6, Manufacturer = "FAG", Model = "6205", RollerCount_n = 9, RollerDiameter_d = 7.94, PitchDiameter_D = 39.04, ContactAngle_alpha = 0, BPFO_Multiplier = 3.585, BPFI_Multiplier = 5.415, BSF_Multiplier = 2.311, FTF_Multiplier = 0.398 },
                new BearingInfo { Id = 7, Manufacturer = "SKF", Model = "NU205", RollerCount_n = 13, RollerDiameter_d = 7.50, PitchDiameter_D = 38.50, ContactAngle_alpha = 0, BPFO_Multiplier = 5.810, BPFI_Multiplier = 7.190, BSF_Multiplier = 2.510, FTF_Multiplier = 0.447 },
                new BearingInfo { Id = 8, Manufacturer = "SKF", Model = "7311B", RollerCount_n = 14, RollerDiameter_d = 12.00, PitchDiameter_D = 69.00, ContactAngle_alpha = 40, BPFO_Multiplier = 5.112, BPFI_Multiplier = 8.888, BSF_Multiplier = 2.278, FTF_Multiplier = 0.365 }
            );
        }
    }
}
