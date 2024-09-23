using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Synchronisation;

public class FullSyncTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; }
    private Mock<JimDbContext> MockJimDbContext { get; set; }
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } 
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; }
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } 
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; }
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; }
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; }
    private List<SyncRule> SyncRulesData { get; set; }
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; }
    private JimApplication Jim { get; set; }
    #endregion
    
    [SetUp]
    public void Setup()
    {
        // common dependencies
        TestUtilities.SetEnvironmentVariables();
        InitiatedBy = TestUtilities.GetInitiatedBy();
        
        // set up the connected systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.AsQueryable().BuildMockDbSet();
        
        // todo: not sure if we need this. remove if not
        // set up the connected system object types mock. this acts as the persisted schema in JIM
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.AsQueryable().BuildMockDbSet();
        
        // set up the metaverse object types mock
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.AsQueryable().BuildMockDbSet();
        
        // set up the sync rule stub mocks. they will be customised to specific use-cases in individual tests.
        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.AsQueryable().BuildMockDbSet();
        
        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);
        
        // instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }
    
    // [Test]
    // public async Task PendingExportTestSuccessAsync()
    // {
    //      // set up the Pending Export objects mock. this is specific to this test
    //      var pendingExportObjects = new List<PendingExport>
    //      {
    //          new()
    //          {
    //              Id = Guid.NewGuid(),
    //              ConnectedSystemId = 1,
    //              Status = PendingExportStatus.Pending,
    //              ChangeType = PendingExportChangeType.Create,
    //              AttributeValueChanges = new()
    //              {
    //                  new()
    //                  {
    //                      Id = Guid.NewGuid(),
    //                      ChangeType = PendingExportAttributeChangeType.Add,
    //                      AttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME,
    //                      StringValue = "James McGill"
    //                  },
    //                  
    //              }
    //          }
    //     };
    //     
    //     // mock up a connector that will return testable data
    //     
    //     // now execute Jim functionality we want to test...
    //     
    //     // confirm the results persisted to the mocked db context
    //
    //     // validate the first user (who is a manager)
    //
    //     // validate the second user (who is a direct-report)
    //     
    //     // validate second user manager reference
    //
    //     Assert.Fail("Not implemented yet. Doesn't need to be done until export scenario worked on.");
    // }
    
    // todo: CSO obsolete process
    // todo: CSO joins to MVO
    // todo: CSO projects to MV
    // todo: MVO has pending attribute value adds for all data types as expected
    // todo: MVO has pending attribute value removes for all data types as expected
    // todo: MVO changes are persisted as expected
    // todo: DELETIONS????
    // todo: Onward updates as a result of all above scenarios
}