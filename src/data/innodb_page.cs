using System.IO;
var INNODB_PAGE_SIZE = 1024 * 16; //# InnoDB Page 16K
                                  // File Header
var FIL_PAGE_SPACE_OR_CHKSUM = 0;

var FIL_PAGE_OFFSET = 4;

var FIL_PAGE_PREV = 8;

var FIL_PAGE_NEXT = 12;

var FIL_PAGE_LSN = 16;

var FIL_PAGE_TYPE = 24;
// 页类型
var innodb_page_type = new Dictionary<string, string>(){
    {"0000","Freshly Allocated Page"},
    {"0002","Undo Log Page"},
    {"0003","File Segment inode"},
    {"0004","Insert Buffer Free List"},
    {"0005","Insert Buffer Bitmap"},
    {"0006","System Page"},
    {"0007","Transaction system Page"},
    {"0008","File Space Header"},
    {"0009","extend description page"},
    {"000a","Uncompressed BLOB Page"},
    {"000b","1st compressed BLOB Page"},
    {"000c","Subsequent compressed BLOB Page"},
    {"45bf","B-tree Node"},
    {"45bd","Tablespace SDI Index page"}
    };



var FIL_PAGE_FILE_FLUSH_LSN = 26;

var FIL_PAGE_ARCH_LOG_NO_OR_SPACE_ID = 34;

var FIL_PAGE_DATA = 38;//文件头部
                       //#  Page Header


var PAGE_N_DIR_SLOTS = 0;

var PAGE_HEAP_TOP = 2;

var PAGE_N_HEAP = 4;

var PAGE_FREE = 6;

var PAGE_GARBAGE = 8;


var PAGE_LAST_INSERT = 10;


var PAGE_DIRECTION = 12;

var PAGE_N_DIRECTION = 14;

var PAGE_N_RECS = 16;

var PAGE_MAX_TRX_ID = 18;

var PAGE_LEVEL = 26;


var PAGE_INDEX_ID = 28;

var PAGE_BTR_SEG_LEAF = 38;

var PAGE_BTR_SEG_TOP = 48;









var PAGE_PAGE_DATA = 56; //页面头部


var Infimum_Supremum_PAGE_DATA = 26; // 最小记录和最大记录


var File_Tailer = 8; // 最小记录和最大记录


var innodb_page_direction = new Dictionary<string, string>(){
    {"0000","Unknown(0x0000)"},
    {"0001", "Page Left"},
    {"0002", "Page Right"},
    {"0003", "Page Same Rec"},
    {"0004", "Page Same Page"},
    {"0005", "Page No Direction"},
    {"ffff", "Unkown2(0xffff)"}
};
string MachReadFromN(byte[] page, int start_offset, int length)
{
    var ret = page[start_offset..(start_offset + length)];
    return Convert.ToHexString(ret).ToLower();
}
void GetInnodbPageType()
{
    var f = new FileInfo(@"F:\d_23030412514284807_vib#P#p24.ibd");



    var fsize = f.Length / INNODB_PAGE_SIZE;
    dynamic ret = new { };
    var read = new BinaryReader(f.OpenRead());
    for (int i = 0; i < fsize; i++)
    {

        var page = read.ReadBytes(INNODB_PAGE_SIZE);
        //file header
        var page_offset = MachReadFromN(page, FIL_PAGE_OFFSET, 4);
        var page_pre = MachReadFromN(page, FIL_PAGE_OFFSET + 4, 4);
        var page_next = MachReadFromN(page, FIL_PAGE_OFFSET + 8, 4);
        var page_lsn = MachReadFromN(page, FIL_PAGE_OFFSET + 12, 8);

        var page_type = MachReadFromN(page, FIL_PAGE_TYPE, 2);
        var flush_lsn = MachReadFromN(page, FIL_PAGE_TYPE + 2, 8);
        var space_id = MachReadFromN(page, FIL_PAGE_TYPE + 10, 4);

        if (page_type != "45bf")
        {
            continue;
        }

        display($"page offset [{page_offset}], page type <{innodb_page_type[page_type]}>, pre page [{page_pre}], next page [{page_next}] , flush_lsn <{flush_lsn}> , space_id <{space_id}>");

        //page header
        var page_dir_slots = MachReadFromN(page, FIL_PAGE_DATA, 2);
        var page_heap_top = MachReadFromN(page, FIL_PAGE_DATA + 2, 2);
        var page_n_heap = MachReadFromN(page, FIL_PAGE_DATA + 4, 2);
        var page_n_recs = MachReadFromN(page, PAGE_N_RECS + 4, 2);
        var page_level = MachReadFromN(page, FIL_PAGE_DATA + 26, 2);
        var page_index_id = MachReadFromN(page, FIL_PAGE_DATA + 28, 8);
        var page_btr_seg_leaf = MachReadFromN(page, FIL_PAGE_DATA + 36, 10);
        var page_btr_seg_top = MachReadFromN(page, FIL_PAGE_DATA + 46, 10);

        display($"page_dir_slots {page_dir_slots}  page_heap_top {page_heap_top}  page_n_heap {page_n_heap}  page_n_recs {page_n_recs} page_level {page_level} page_index_id {page_index_id} page_btr_seg_leaf {page_btr_seg_leaf} page_btr_seg_top {page_btr_seg_top}");
        //Infimum + Supremum


        //User Records






        var reserve1 = MachReadFromN(page, FIL_PAGE_DATA + PAGE_PAGE_DATA + Infimum_Supremum_PAGE_DATA, 1);
        var reserv2 = MachReadFromN(page, FIL_PAGE_DATA + PAGE_PAGE_DATA + Infimum_Supremum_PAGE_DATA + 1, 1);
        var delete_mask = MachReadFromN(page, FIL_PAGE_DATA + PAGE_PAGE_DATA + Infimum_Supremum_PAGE_DATA + 2, 1);
        var min_rec_mask = MachReadFromN(page, FIL_PAGE_DATA + PAGE_PAGE_DATA + Infimum_Supremum_PAGE_DATA + 3, 1);
        display($"reserve1 {reserve1} reserv2 {reserv2} delete_mask {delete_mask} min_rec_mask  {min_rec_mask}");
        // Free Space

        // Page Directory

        //File Trailer
        var file_trailer = MachReadFromN(page, INNODB_PAGE_SIZE - 8, 8);



    }
}
GetInnodbPageType();