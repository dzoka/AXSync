Project to synchronize Dynamics AX 2009 to external system by means of named pipes and SQL server.

It consists of two parts, first - client, which receives messages from Dynamics AX 2009 by named pipe, and second - Windows service to collect submitions from the client into queue and subsequently submit them to SQL server.

Sample usage in Dynamics AX 2009 is like this:
```x++
Set         permSet = new Set(Types::Class);
CustTable   custTable;
;
try
{
  permSet.add(new InteropPermission(InteropKind::ClrInterop));
  CodeAccessPermission::assertMultiple(permSet);
  
  Dzoka.AxSyncLibrary.Client::Submit(strfmt("CUSTOMER;%1;%2", custTable.AccountNum, strReplace(custTable.Name,"'",' ')));
  
  CodeAccessPermission::revertAssert();
}
catch (Exception::CLRError)
{
  ex = ClrInterOp::getLastException();
  if (ex != null)
  {
    ex = ex.get_InnerException();
    info(ex.ToString());
  }
}
   
             
