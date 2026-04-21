# Parking Management System (PMS) Ver 2.0

He thong quan ly bai do xe su dung ASP.NET Core MVC + SQL Server.

## 1) Yeu cau moi truong

- Windows 10/11
- .NET SDK 8.0
- SQL Server (Express/Developer deu duoc)
- SSMS hoac Azure Data Studio

## 2) Cau truc project

- File solution: `ParkingManagementSystem/ParkingManagementSystem.sln`
- App chinh: `ParkingManagementSystem/ParkingManagementSystem`
- Script database: `ParkingManagementDB.sql`

## 3) Tao database tu script co san

1. Mo SQL Server Management Studio (SSMS), ket noi den SQL Server instance cua ban.
2. Mo file `ParkingManagementDB.sql`.
3. Chay toan bo script.
4. Kiem tra da co database `ParkingManagementDB`.

Luu y: script da co du lieu mau (Users, VehicleTypes, ParkingPositions, ...).

## 4) Cau hinh chuoi ket noi (Connection String)

Mo file:

- `ParkingManagementSystem/ParkingManagementSystem/appsettings.json`

Cap nhat `ConnectionStrings:DefaultConnection` theo SQL Server instance may ban.

Vi du:

```json
"ConnectionStrings": {
  "DefaultConnection": "Server=.\\SQLEXPRESS;Database=ParkingManagementDB;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
}
```

Neu ban dung SQL Login:

```json
"ConnectionStrings": {
  "DefaultConnection": "Server=.\\SQLEXPRESS;Database=ParkingManagementDB;User Id=sa;Password=your_password;MultipleActiveResultSets=true;TrustServerCertificate=True"
}
```

## 5) Chay ung dung

Sau khi da mo va setting xong sql hay mo project trong Visual studio roi chay file 'ParkingManagementSystem.sln'
roi an f5 de chay thu, sau khi hien ra trang chu dang nhap hay nhap tai khoan mau va an dang nhap

## 6) Tai khoan dang nhap mau

He thong dang nhap su dung bang `Users`.

Du lieu mau (co the thay doi theo DB cua ban):

- `admin1 / 123456` (Role: `Admin`)
- `user1 / 123456` (Role: `Staff`)
- `USER2 / 123456` (Role: `Staff`)

Luu y: can chon dung Role tren man hinh dang nhap.

## 7) Huong dan test nhanh luong vao/ra

### Xe vao bai

1. Vao man hinh `Tram Kiem Soat Vao/Ra`.
2. Quet/nhan dien bien so tu camera.
3. Bam `XAC NHAN CHO VAO`.
4. He thong tao phien gui xe va gan ma RFID.

### Xe ra bai

1. Tai lan ra, quet bien so.
2. He thong doi chieu phien dang hoat dong theo bien so.
3. Tu dong dien RFID da cap luc xe vao.
4. Bam `XAC NHAN CHO RA` de hoan tat va tinh phi.

### Lich su ra vao

- Vao trang `Lich su ra vao` de theo doi cac phien.
- Co cac thao tac:
  - `Xu ly trung` (dong cac phien trung bien so, giu phien moi nhat)
  - `Xoa` (xoa tung phien)
  - `Xoa toan bo lich su`

## 8) Loi thuong gap

### Khong ket noi duoc database

- Kiem tra SQL Server service dang chay.
- Kiem tra lai `DefaultConnection`.
- Kiem tra ten instance SQL co dung khong.

### Build loi file bi lock (MSB3021 / MSB3027)

- Nguyen nhan: app dang chay o process cu.
- Cach xu ly: dung process cu roi chay lai `dotnet build`.

### Quet bien so ra nhung khong thay RFID

- Kiem tra phien vao da duoc tao chua.
- Kiem tra du lieu test bi trung trong `Lich su ra vao`.
- Thu `Xu ly trung` hoac `Xoa` cac phien test cu roi test lai.

## 9) Luu y bao mat

- Khong de token API va mat khau that trong source code.
- Neu trien khai that, nen dua thong tin nhay cam vao Secret Manager hoac bien moi truong.

### Cau hinh Plate Recognizer token an toan

Ung dung doc token theo uu tien:

1. Bien moi truong `PLATE_RECOGNIZER_TOKEN`
2. `PlateRecognizer:ApiToken` trong `appsettings.json`

Khuyen nghi chi dung bien moi truong, vi du (PowerShell):

```powershell
$env:PLATE_RECOGNIZER_TOKEN="your_new_token_here"
dotnet run
```