"""Read-only SQLite plan evidence using the exact native library under comparison."""
import argparse,ctypes as c,json,pathlib,hashlib
p=argparse.ArgumentParser();p.add_argument('library');p.add_argument('database');p.add_argument('output');a=p.parse_args()
lib=c.CDLL(str(pathlib.Path(a.library).resolve()))
def api(name,args,result):
 f=getattr(lib,name);f.argtypes=args;f.restype=result;return f
open_db=api('sqlite3_open_v2',[c.c_char_p,c.POINTER(c.c_void_p),c.c_int,c.c_char_p],c.c_int)
prepare=api('sqlite3_prepare_v2',[c.c_void_p,c.c_char_p,c.c_int,c.POINTER(c.c_void_p),c.c_void_p],c.c_int)
step=api('sqlite3_step',[c.c_void_p],c.c_int);finalize=api('sqlite3_finalize',[c.c_void_p],c.c_int)
count=api('sqlite3_column_count',[c.c_void_p],c.c_int);kind=api('sqlite3_column_type',[c.c_void_p,c.c_int],c.c_int)
integer=api('sqlite3_column_int64',[c.c_void_p,c.c_int],c.c_int64);text=api('sqlite3_column_text',[c.c_void_p,c.c_int],c.c_char_p)
error=api('sqlite3_errmsg',[c.c_void_p],c.c_char_p);close=api('sqlite3_close',[c.c_void_p],c.c_int)
version=api('sqlite3_libversion',[],c.c_char_p)
db=c.c_void_p();assert open_db(str(pathlib.Path(a.database).resolve()).encode(),c.byref(db),1,None)==0
def query(sql):
 stmt=c.c_void_p();result=prepare(db,sql.encode(),-1,c.byref(stmt),None)
 if result:raise RuntimeError(error(db).decode())
 rows=[]
 try:
  while True:
   result=step(stmt)
   if result==101:break
   if result!=100:raise RuntimeError(error(db).decode())
   rows.append([None if kind(stmt,i)==5 else integer(stmt,i) if kind(stmt,i)==1 else text(stmt,i).decode() for i in range(count(stmt))])
 finally:finalize(stmt)
 return rows
queries={
 'sql_point':'SELECT id,index_key,value FROM records WHERE id=@id',
 'sql_projection':'SELECT id FROM records WHERE id=@id',
 'sql_range20':'SELECT id,index_key,value FROM records WHERE index_key>=@lo AND index_key<=@hi ORDER BY index_key,id LIMIT 20',
 'sql_count':'SELECT COUNT(*) FROM records',
 'sql_group':'SELECT index_key,COUNT(*) FROM records GROUP BY index_key ORDER BY index_key',
 'sql_join':'SELECT u.id,u.name,o.id,o.amount FROM users u JOIN orders o ON u.id=o.user_id WHERE u.id=@id ORDER BY o.id LIMIT 20',
 'json_index':"SELECT id,json_extract(body,'$.name') FROM docs WHERE json_extract(body,'$.age')=@id ORDER BY id LIMIT 20",
 'json_scan':"SELECT COUNT(*) FROM docs WHERE json_extract(body,'$.age')%2=0"}
try:
 data={'SQLite':version().decode(),'NativeSHA256':hashlib.sha256(pathlib.Path(a.library).read_bytes()).hexdigest().upper(),'Queries':{name:{'SQL':sql,'QueryPlan':query('EXPLAIN QUERY PLAN '+sql),'Bytecode':query('EXPLAIN '+sql)} for name,sql in queries.items()}}
finally:close(db)
output=pathlib.Path(a.output);output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8-sig')
print('Saved exact-library EXPLAIN evidence:',output)
